using System.Diagnostics;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

internal sealed class RedisDistributedLease : IOperationLease
{
    private static readonly LuaScript RenewScript = LuaScript.Prepare(
        "if redis.call('get', @key) == @token then return redis.call('pexpire', @key, @leaseMilliseconds) else return 0 end");
    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(
        "if redis.call('get', @key) == @token then return redis.call('del', @key) else return 0 end");

    private readonly IDatabase database;
    private readonly RedisKey key;
    private readonly RedisValue token;
    private readonly TimeSpan leaseDuration;
    private readonly CancellationTokenSource renewalCancellation = new();
    private readonly CancellationTokenSource leaseLostCancellation = new();
    private readonly CancellationToken leaseLostToken;
    private readonly object disposeGate = new();
    private readonly Task renewalTask;
    private long validUntilTimestamp;
    private Exception? renewalFailure;
    private Task? disposeTask;

    private RedisDistributedLease(
        IDatabase database,
        RedisKey key,
        RedisValue token,
        TimeSpan leaseDuration,
        long validUntilTimestamp)
    {
        this.database = database;
        this.key = key;
        this.token = token;
        this.leaseDuration = leaseDuration;
        this.validUntilTimestamp = validUntilTimestamp;
        leaseLostToken = leaseLostCancellation.Token;
        renewalTask = RenewUntilDisposedAsync();
    }

    public CancellationToken LeaseLost => leaseLostToken;
    internal RedisKey Key => key;
    internal RedisValue Token => token;

    public static async ValueTask<RedisDistributedLease> AcquireAsync(
        IDatabase database,
        RedisKey key,
        CancellationToken cancellationToken)
    {
        return await AcquireAsync(database, key, TimeSpan.FromSeconds(30), cancellationToken);
    }

    internal static async ValueTask<RedisDistributedLease> AcquireAsync(
        IDatabase database,
        RedisKey key,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateDuration(leaseDuration);
        var token = Guid.NewGuid().ToString("N");
        long acquiredAt;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            acquiredAt = Stopwatch.GetTimestamp();
            if (await AwaitAcquisitionAsync(
                    database,
                    key,
                    token,
                    leaseDuration,
                    acquiredAt,
                    cancellationToken))
            {
                break;
            }

            await Task.Delay(Random.Shared.Next(20, 75), cancellationToken);
        }

        var lease = new RedisDistributedLease(
            database,
            key,
            token,
            leaseDuration,
            acquiredAt + ToStopwatchTicks(leaseDuration));
        await lease.ValidateAcquisitionAsync(cancellationToken);
        return lease;
    }

    public static async ValueTask<RedisDistributedLease?> TryAcquireAsync(
        IDatabase database,
        RedisKey key,
        CancellationToken cancellationToken)
    {
        return await TryAcquireAsync(database, key, TimeSpan.FromSeconds(30), cancellationToken);
    }

    internal static async ValueTask<RedisDistributedLease?> TryAcquireAsync(
        IDatabase database,
        RedisKey key,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDuration(leaseDuration);
        var token = Guid.NewGuid().ToString("N");
        var acquiredAt = Stopwatch.GetTimestamp();
        if (!await AwaitAcquisitionAsync(
                database,
                key,
                token,
                leaseDuration,
                acquiredAt,
                cancellationToken))
        {
            return null;
        }

        var lease = new RedisDistributedLease(
            database,
            key,
            token,
            leaseDuration,
            acquiredAt + ToStopwatchTicks(leaseDuration));
        await lease.ValidateAcquisitionAsync(cancellationToken);
        return lease;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await renewalCancellation.CancelAsync();
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MarkLost(exception);
        }

        try
        {
            var released = (int)await database.ScriptEvaluateAsync(
                ReleaseScript,
                new { key, token });
            if (released == 0)
            {
                MarkLost(new InvalidOperationException(
                    $"Die verteilte Redis-Sperre {key} war beim Freigeben nicht mehr im Besitz dieses Workers."));
            }
        }
        catch (Exception exception)
        {
            MarkLost(exception);
        }
        finally
        {
            renewalCancellation.Dispose();
            leaseLostCancellation.Dispose();
        }
    }

    public void ThrowIfLost()
    {
        if (RemainingLeaseTime() <= TimeSpan.Zero && !leaseLostCancellation.IsCancellationRequested)
        {
            MarkLost(new TimeoutException($"Die verteilte Redis-Sperre {key} ist abgelaufen."));
        }

        if (leaseLostCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Die verteilte Redis-Sperre {key} wurde verloren.",
                Volatile.Read(ref renewalFailure));
        }
    }

    internal void ThrowFenceLost(string operation)
    {
        MarkLost(new InvalidOperationException(
            $"Die Redis-Sperre {key} war beim atomaren Vorgang '{operation}' nicht mehr gültig."));
        ThrowIfLost();
    }

    private async Task ValidateAcquisitionAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && RemainingLeaseTime() > TimeSpan.Zero)
        {
            return;
        }

        Exception? releaseFailure = null;
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            releaseFailure = exception;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Die Redis-Sperre wurde während der Übernahme abgebrochen.",
                releaseFailure,
                cancellationToken);
        }

        throw new TimeoutException(
            $"Die verteilte Redis-Sperre {key} war bei Rückgabe bereits abgelaufen.",
            releaseFailure);
    }

    private async Task RenewUntilDisposedAsync()
    {
        while (!renewalCancellation.IsCancellationRequested)
        {
            var remainingBeforeWait = RemainingLeaseTime();
            if (remainingBeforeWait <= TimeSpan.Zero)
            {
                LoseLease(new TimeoutException(
                    $"Die verteilte Redis-Sperre {key} ist vor der Erneuerung abgelaufen."));
            }

            var regularDelay = leaseDuration / 3;
            var delay = remainingBeforeWait <= regularDelay
                ? TimeSpan.FromTicks(Math.Max(1, remainingBeforeWait.Ticks / 3))
                : regularDelay;
            await Task.Delay(delay, renewalCancellation.Token);

            Exception? lastFailure = null;
            while (!renewalCancellation.IsCancellationRequested)
            {
                try
                {
                    var remaining = RemainingLeaseTime();
                    if (remaining <= TimeSpan.Zero)
                    {
                        LoseLease(new TimeoutException(
                            $"Die verteilte Redis-Sperre {key} ist vor der Erneuerung abgelaufen."));
                    }

                    var renewalStartedAt = Stopwatch.GetTimestamp();
                    var renewal = database.ScriptEvaluateAsync(
                        RenewScript,
                        new
                        {
                            key,
                            token,
                            leaseMilliseconds = (long)leaseDuration.TotalMilliseconds
                        });
                    remaining = RemainingLeaseTime();
                    if (remaining <= TimeSpan.Zero)
                    {
                        ObserveFailure(renewal);
                        LoseLease(new TimeoutException(
                            $"Die verteilte Redis-Sperre {key} ist während des Erneuerungsstarts abgelaufen."));
                    }

                    var deadline = Task.Delay(remaining, renewalCancellation.Token);
                    if (await Task.WhenAny(renewal, deadline) != renewal)
                    {
                        ObserveFailure(renewal);
                        renewalCancellation.Token.ThrowIfCancellationRequested();
                        LoseLease(new TimeoutException(
                            $"Die Erneuerung der verteilten Redis-Sperre {key} hat ihre Ablaufgrenze überschritten."));
                    }

                    var renewed = (int)await renewal;
                    if (leaseLostCancellation.IsCancellationRequested)
                    {
                        ThrowIfLost();
                    }

                    if (renewed == 0)
                    {
                        LoseLease(new InvalidOperationException(
                            $"Die verteilte Redis-Sperre {key} gehört nicht mehr diesem Worker."));
                    }

                    Volatile.Write(
                        ref validUntilTimestamp,
                        renewalStartedAt + ToStopwatchTicks(leaseDuration));
                    if (RemainingLeaseTime() <= TimeSpan.Zero)
                    {
                        LoseLease(new TimeoutException(
                            $"Die Antwort zur Erneuerung der Redis-Sperre {key} kam nach ihrer sicheren Ablaufgrenze."));
                    }
                    break;
                }
                catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (leaseLostCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                    var remaining = RemainingLeaseTime();
                    if (remaining <= TimeSpan.Zero)
                    {
                        LoseLease(new InvalidOperationException(
                            $"Die verteilte Redis-Sperre {key} konnte vor Ablauf nicht erneuert werden.",
                            lastFailure));
                    }

                    var retryDelay = TimeSpan.FromMilliseconds(
                        Math.Min(250, Math.Max(1, remaining.TotalMilliseconds / 4)));
                    await Task.Delay(
                        retryDelay < remaining ? retryDelay : remaining,
                        renewalCancellation.Token);
                }
            }
        }
    }

    private TimeSpan RemainingLeaseTime()
    {
        var remainingTicks = Volatile.Read(ref validUntilTimestamp) - Stopwatch.GetTimestamp();
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    private void LoseLease(Exception exception)
    {
        MarkLost(exception);
        throw exception;
    }

    private void MarkLost(Exception exception)
    {
        Interlocked.CompareExchange(ref renewalFailure, exception, null);
        try
        {
            renewalCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            ObserveFailure(leaseLostCancellation.CancelAsync());
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<bool> AwaitAcquisitionAsync(
        IDatabase database,
        RedisKey key,
        RedisValue token,
        TimeSpan leaseDuration,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var remainingTicks = startedAt + ToStopwatchTicks(leaseDuration) - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            throw new TimeoutException($"Die Übernahme der verteilten Redis-Sperre {key} hat ihre Ablaufgrenze überschritten.");
        }

        var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(remaining);
        var acquisition = database.StringSetAsync(key, token, leaseDuration, When.NotExists);
        var timeout = Task.Delay(Timeout.InfiniteTimeSpan, attemptCancellation.Token);
        var completed = await Task.WhenAny(acquisition, timeout);
        if (completed == acquisition)
        {
            attemptCancellation.Cancel();
            return await acquisition;
        }

        ReleaseLateAcquisition(database, key, token, acquisition);
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        throw new TimeoutException($"Die Übernahme der verteilten Redis-Sperre {key} hat ihre Ablaufgrenze überschritten.");
    }

    private static void ReleaseLateAcquisition(
        IDatabase database,
        RedisKey key,
        RedisValue token,
        Task<bool> acquisition) =>
        _ = ReleaseLateAcquisitionAsync(database, key, token, acquisition);

    private static async Task ReleaseLateAcquisitionAsync(
        IDatabase database,
        RedisKey key,
        RedisValue token,
        Task<bool> acquisition)
    {
        try
        {
            if (await acquisition)
            {
                await database.ScriptEvaluateAsync(ReleaseScript, new { key, token });
            }
        }
        catch
        {
            // A timed-out acquisition is no longer usable; Redis TTL remains the final safety net.
        }
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        checked((long)(duration.TotalSeconds * Stopwatch.Frequency));

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Der Redis-Lease muss mindestens 30 ms laufen.");
        }
    }
}
