using System.Collections.Concurrent;
using System.Diagnostics;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisProfileAccessGate(IConnectionMultiplexer redis) : IProfileAccessGate
{
    private const string Prefix = "keywars:{profile-access}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeletedMarkerLifetime = TimeSpan.FromHours(24);
    private static readonly LuaScript AcquireScript = LuaScript.Prepare(
        "local state = redis.call('get', @stateKey); " +
        "if state == 'deleted' then return -1 end; " +
        "if state then return 0 end; " +
        "redis.call('zremrangebyscore', @activeKey, '-inf', @now); " +
        "redis.call('zadd', @activeKey, @expiresAt, @token); " +
        "redis.call('pexpire', @activeKey, @setLifetime); return 1");
    private static readonly LuaScript RenewLeaseScript = LuaScript.Prepare(
        "if redis.call('zscore', @activeKey, @token) then " +
        "redis.call('zadd', @activeKey, @expiresAt, @token); " +
        "redis.call('pexpire', @activeKey, @setLifetime); return 1 else return 0 end");
    private static readonly LuaScript ReleaseLeaseScript = LuaScript.Prepare(
        "return redis.call('zrem', @activeKey, @token)");
    private static readonly LuaScript BeginOperationScript = LuaScript.Prepare(
        "local state = redis.call('get', @stateKey); " +
        "if state == 'deleted' then return -1 end; " +
        "if state then return 0 end; " +
        "redis.call('zremrangebyscore', @activeKey, '-inf', @now); " +
        "redis.call('set', @stateKey, @operationToken, 'PX', @operationLifetime); return 1");
    private static readonly LuaScript RenewOperationScript = LuaScript.Prepare(
        "if redis.call('get', @stateKey) == @operationToken then " +
        "return redis.call('pexpire', @stateKey, @operationLifetime) else return 0 end");
    private static readonly LuaScript CountActiveScript = LuaScript.Prepare(
        "redis.call('zremrangebyscore', @activeKey, '-inf', @now); " +
        "return redis.call('zcard', @activeKey)");
    private static readonly LuaScript CompleteOperationScript = LuaScript.Prepare(
        "if redis.call('get', @stateKey) == @operationToken then " +
        "return redis.call('del', @stateKey) else return 0 end");
    private static readonly LuaScript MarkDeletedScript = LuaScript.Prepare(
        "if redis.call('get', @stateKey) ~= @operationToken then return 0 end; " +
        "redis.call('zremrangebyscore', @activeKey, '-inf', @now); " +
        "if redis.call('zcard', @activeKey) ~= 0 then return -1 end; " +
        "redis.call('set', @stateKey, 'deleted', 'PX', @deletedLifetime); return 1");

    private readonly IDatabase database = redis.GetDatabase();
    private readonly ConcurrentDictionary<Guid, OperationLease> operations = new();

    public async ValueTask<ProfileAccessState> GetStateAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await database.StringGetAsync(StateKey(profileId));
        if (state.IsNull)
        {
            return ProfileAccessState.Available;
        }

        return state == "deleted"
            ? ProfileAccessState.Deleted
            : ProfileAccessState.OperationInProgress;
    }

    public async ValueTask<IOperationLease> AcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var acquisitionStartedAt = Stopwatch.GetTimestamp();
        var result = (int)await database.ScriptEvaluateAsync(
            AcquireScript,
            new
            {
                stateKey = StateKey(profileId),
                activeKey = ActiveKey(profileId),
                token,
                now,
                expiresAt = now + (long)LeaseDuration.TotalMilliseconds,
                setLifetime = (long)(LeaseDuration.TotalMilliseconds * 4)
            });
        return result switch
        {
            1 => await AccessLease.CreateAsync(
                database,
                ActiveKey(profileId),
                token,
                acquisitionStartedAt,
                cancellationToken),
            -1 => throw new ProfileOperationException("profile_deleted", "Dieses Profil wurde bereits gelöscht."),
            _ => throw new ProfileOperationException(
                "profile_operation_in_progress",
                "Für dieses Profil läuft bereits eine Datenschutzoperation.")
        };
    }

    public async ValueTask<IOperationLease> AcquireManyAsync(
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        var leases = new List<IOperationLease>();
        try
        {
            foreach (var profileId in profileIds.Distinct().Order())
            {
                leases.Add(await AcquireAsync(profileId, cancellationToken));
            }

            return new CompositeLease(leases);
        }
        catch
        {
            await DisposeReverseAsync(leases);
            throw;
        }
    }

    public async ValueTask<IOperationLease?> TryBeginOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = $"op:{Guid.NewGuid():N}";
        var acquisitionStartedAt = Stopwatch.GetTimestamp();
        var result = (int)await database.ScriptEvaluateAsync(
            BeginOperationScript,
            new
            {
                stateKey = StateKey(profileId),
                activeKey = ActiveKey(profileId),
                operationToken = token,
                operationLifetime = (long)OperationDuration.TotalMilliseconds,
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        if (result != 1)
        {
            return null;
        }

        var operation = await OperationLease.CreateAsync(
            database,
            operations,
            profileId,
            token,
            acquisitionStartedAt,
            cancellationToken);
        if (operations.TryAdd(profileId, operation))
        {
            return operation;
        }

        await operation.DisposeAsync();
        return null;
    }

    public async Task WaitForIdleAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!operations.ContainsKey(profileId))
        {
            throw new InvalidOperationException("Für dieses Profil wurde keine exklusive Operation begonnen.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = (long)await database.ScriptEvaluateAsync(
                CountActiveScript,
                new
                {
                    activeKey = ActiveKey(profileId),
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            if (active == 0)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    public async ValueTask CompleteOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operations.TryRemove(profileId, out var operation))
        {
            await operation.DisposeAsync();
        }
    }

    public async ValueTask MarkDeletedAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!operations.TryGetValue(profileId, out var operation))
        {
            throw new InvalidOperationException("Für dieses Profil wurde keine exklusive Operation begonnen.");
        }

        await operation.MarkDeletedAsync(cancellationToken);
    }

    private static RedisKey StateKey(Guid profileId) => $"{Prefix}:{profileId:N}:state";
    private static RedisKey ActiveKey(Guid profileId) => $"{Prefix}:{profileId:N}:active";

    private static async ValueTask DisposeReverseAsync(IReadOnlyList<IOperationLease> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync();
        }
    }

    private sealed class AccessLease : IOperationLease
    {
        private readonly IDatabase database;
        private readonly RedisKey activeKey;
        private readonly RedisValue token;
        private readonly CancellationTokenSource renewalCancellation = new();
        private readonly CancellationTokenSource leaseLostCancellation = new();
        private readonly CancellationToken leaseLostToken;
        private readonly object disposeGate = new();
        private readonly Task renewalTask;
        private long validUntilTimestamp;
        private Exception? renewalFailure;
        private Task? disposeTask;

        private AccessLease(
            IDatabase database,
            RedisKey activeKey,
            RedisValue token,
            long validUntilTimestamp)
        {
            this.database = database;
            this.activeKey = activeKey;
            this.token = token;
            this.validUntilTimestamp = validUntilTimestamp;
            leaseLostToken = leaseLostCancellation.Token;
            renewalTask = RenewAsync();
        }

        public CancellationToken LeaseLost => leaseLostToken;

        public static async ValueTask<AccessLease> CreateAsync(
            IDatabase database,
            RedisKey activeKey,
            RedisValue token,
            long acquisitionStartedAt,
            CancellationToken cancellationToken)
        {
            var lease = new AccessLease(
                database,
                activeKey,
                token,
                acquisitionStartedAt + ToStopwatchTicks(LeaseDuration));
            if (!cancellationToken.IsCancellationRequested && lease.RemainingLeaseTime() > TimeSpan.Zero)
            {
                return lease;
            }

            await lease.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Der verteilte Profilzugriffs-Lease war bei Rückgabe bereits abgelaufen.");
        }

        public void ThrowIfLost()
        {
            if (RemainingLeaseTime() <= TimeSpan.Zero && !leaseLostCancellation.IsCancellationRequested)
            {
                MarkLost(new TimeoutException("Der verteilte Profilzugriffs-Lease ist abgelaufen."));
            }

            if (leaseLostCancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Der verteilte Profilzugriffs-Lease wurde verloren.",
                    Volatile.Read(ref renewalFailure));
            }
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
            catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkLost(exception);
            }

            try
            {
                var released = (int)await database.ScriptEvaluateAsync(
                    ReleaseLeaseScript,
                    new { activeKey, token });
                if (released == 0)
                {
                    MarkLost(new InvalidOperationException(
                        "Der verteilte Profilzugriffs-Lease war beim Freigeben nicht mehr im Besitz dieses Workers."));
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

        private async Task RenewAsync()
        {
            while (!renewalCancellation.IsCancellationRequested)
            {
                var remaining = RemainingLeaseTime();
                if (remaining <= TimeSpan.Zero)
                {
                    LoseLease(new TimeoutException("Der verteilte Profilzugriffs-Lease ist vor der Erneuerung abgelaufen."));
                }

                var regularDelay = LeaseDuration / 3;
                var delay = remaining <= regularDelay
                    ? TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 3))
                    : regularDelay;
                await Task.Delay(delay, renewalCancellation.Token);

                Exception? lastFailure = null;
                while (!renewalCancellation.IsCancellationRequested)
                {
                    try
                    {
                        remaining = RemainingLeaseTime();
                        if (remaining <= TimeSpan.Zero)
                        {
                            LoseLease(new TimeoutException("Der verteilte Profilzugriffs-Lease ist vor der Erneuerung abgelaufen."));
                        }

                        var renewalStartedAt = Stopwatch.GetTimestamp();
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var renewal = database.ScriptEvaluateAsync(
                            RenewLeaseScript,
                            new
                            {
                                activeKey,
                                token,
                                expiresAt = now + (long)LeaseDuration.TotalMilliseconds,
                                setLifetime = (long)(LeaseDuration.TotalMilliseconds * 4)
                            });
                        var deadline = Task.Delay(remaining, renewalCancellation.Token);
                        if (await Task.WhenAny(renewal, deadline) != renewal)
                        {
                            ObserveFailure(renewal);
                            renewalCancellation.Token.ThrowIfCancellationRequested();
                            LoseLease(new TimeoutException(
                                "Die Erneuerung des Profilzugriffs-Lease hat seine Ablaufgrenze überschritten."));
                        }

                        var renewed = (int)await renewal;
                        if (renewed != 1)
                        {
                            LoseLease(new InvalidOperationException(
                                "Der verteilte Profilzugriffs-Lease gehört nicht mehr diesem Worker."));
                        }

                        Volatile.Write(
                            ref validUntilTimestamp,
                            renewalStartedAt + ToStopwatchTicks(LeaseDuration));
                        if (RemainingLeaseTime() <= TimeSpan.Zero)
                        {
                            LoseLease(new TimeoutException(
                                "Die Antwort zur Erneuerung des Profilzugriffs-Lease kam zu spät."));
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
                        remaining = RemainingLeaseTime();
                        if (remaining <= TimeSpan.Zero)
                        {
                            LoseLease(new InvalidOperationException(
                                "Der verteilte Profilzugriffs-Lease konnte vor Ablauf nicht erneuert werden.",
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
                leaseLostCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                renewalCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void ObserveFailure(Task task) =>
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        private static long ToStopwatchTicks(TimeSpan duration) =>
            checked((long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));
    }

    private sealed class OperationLease : IOperationLease
    {
        private readonly IDatabase database;
        private readonly ConcurrentDictionary<Guid, OperationLease> owner;
        private readonly Guid profileId;
        private readonly RedisValue operationToken;
        private readonly CancellationTokenSource renewalCancellation = new();
        private readonly CancellationTokenSource leaseLost = new();
        private readonly CancellationToken leaseLostToken;
        private readonly object disposeGate = new();
        private readonly Task renewalTask;
        private long validUntilTimestamp;
        private Exception? renewalFailure;
        private Task? disposeTask;
        private int deleted;

        private OperationLease(
            IDatabase database,
            ConcurrentDictionary<Guid, OperationLease> owner,
            Guid profileId,
            RedisValue operationToken,
            long validUntilTimestamp)
        {
            this.database = database;
            this.owner = owner;
            this.profileId = profileId;
            this.operationToken = operationToken;
            this.validUntilTimestamp = validUntilTimestamp;
            leaseLostToken = leaseLost.Token;
            renewalTask = RenewAsync();
        }

        public CancellationToken LeaseLost => leaseLostToken;

        public static async ValueTask<OperationLease> CreateAsync(
            IDatabase database,
            ConcurrentDictionary<Guid, OperationLease> owner,
            Guid profileId,
            RedisValue operationToken,
            long acquisitionStartedAt,
            CancellationToken cancellationToken)
        {
            var lease = new OperationLease(
                database,
                owner,
                profileId,
                operationToken,
                acquisitionStartedAt + ToStopwatchTicks(OperationDuration));
            if (!cancellationToken.IsCancellationRequested && lease.RemainingLeaseTime() > TimeSpan.Zero)
            {
                return lease;
            }

            await lease.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Der exklusive Profilzugriff war bei Rückgabe bereits abgelaufen.");
        }

        public void ThrowIfLost()
        {
            if (RemainingLeaseTime() <= TimeSpan.Zero && !leaseLost.IsCancellationRequested)
            {
                MarkLost(new TimeoutException("Der exklusive Profilzugriff ist abgelaufen."));
            }

            if (leaseLost.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Der exklusive Profilzugriff wurde verloren.",
                    Volatile.Read(ref renewalFailure));
            }
        }

        public async ValueTask MarkDeletedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfLost();
            var result = (int)await database.ScriptEvaluateAsync(
                MarkDeletedScript,
                new
                {
                    stateKey = StateKey(profileId),
                    activeKey = ActiveKey(profileId),
                    operationToken,
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    deletedLifetime = (long)DeletedMarkerLifetime.TotalMilliseconds
                });
            if (result != 1)
            {
                throw new InvalidOperationException(
                    result == -1
                        ? "Das Profil besitzt noch aktive Zugriffe."
                        : "Der exklusive Profilzugriff wurde verloren.");
            }

            Interlocked.Exchange(ref deleted, 1);
            await renewalCancellation.CancelAsync();
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
            owner.TryRemove(KeyValuePair.Create(profileId, this));
            await renewalCancellation.CancelAsync();
            try
            {
                await renewalTask;
            }
            catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkLost(exception);
            }

            if (Volatile.Read(ref deleted) == 0)
            {
                try
                {
                    var released = (int)await database.ScriptEvaluateAsync(
                        CompleteOperationScript,
                        new { stateKey = StateKey(profileId), operationToken });
                    if (released == 0)
                    {
                        MarkLost(new InvalidOperationException(
                            "Der exklusive Profilzugriff war beim Freigeben nicht mehr im Besitz dieses Workers."));
                    }
                }
                catch (Exception exception)
                {
                    MarkLost(exception);
                }
            }

            leaseLost.Dispose();
            renewalCancellation.Dispose();
        }

        private async Task RenewAsync()
        {
            while (!renewalCancellation.IsCancellationRequested)
            {
                if (Volatile.Read(ref deleted) != 0)
                {
                    return;
                }

                var remaining = RemainingLeaseTime();
                if (remaining <= TimeSpan.Zero)
                {
                    LoseLease(new TimeoutException("Der exklusive Profilzugriff ist vor der Erneuerung abgelaufen."));
                }

                var regularDelay = OperationDuration / 3;
                var delay = remaining <= regularDelay
                    ? TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 3))
                    : regularDelay;
                await Task.Delay(delay, renewalCancellation.Token);

                Exception? lastFailure = null;
                while (!renewalCancellation.IsCancellationRequested)
                {
                    try
                    {
                        remaining = RemainingLeaseTime();
                        if (remaining <= TimeSpan.Zero)
                        {
                            LoseLease(new TimeoutException("Der exklusive Profilzugriff ist vor der Erneuerung abgelaufen."));
                        }

                        var renewalStartedAt = Stopwatch.GetTimestamp();
                        var renewal = database.ScriptEvaluateAsync(
                            RenewOperationScript,
                            new
                            {
                                stateKey = StateKey(profileId),
                                operationToken,
                                operationLifetime = (long)OperationDuration.TotalMilliseconds
                            });
                        var deadline = Task.Delay(remaining, renewalCancellation.Token);
                        if (await Task.WhenAny(renewal, deadline) != renewal)
                        {
                            ObserveFailure(renewal);
                            renewalCancellation.Token.ThrowIfCancellationRequested();
                            LoseLease(new TimeoutException(
                                "Die Erneuerung des exklusiven Profilzugriffs hat seine Ablaufgrenze überschritten."));
                        }

                        var renewed = (int)await renewal;
                        if (renewed != 1)
                        {
                            LoseLease(new InvalidOperationException(
                                "Der exklusive Profilzugriff gehört nicht mehr diesem Worker."));
                        }

                        Volatile.Write(
                            ref validUntilTimestamp,
                            renewalStartedAt + ToStopwatchTicks(OperationDuration));
                        if (RemainingLeaseTime() <= TimeSpan.Zero)
                        {
                            LoseLease(new TimeoutException(
                                "Die Antwort zur Erneuerung des exklusiven Profilzugriffs kam zu spät."));
                        }

                        break;
                    }
                    catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception) when (leaseLost.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        lastFailure = exception;
                        remaining = RemainingLeaseTime();
                        if (remaining <= TimeSpan.Zero)
                        {
                            LoseLease(new InvalidOperationException(
                                "Der exklusive Profilzugriff konnte vor Ablauf nicht erneuert werden.",
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
                leaseLost.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                renewalCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void ObserveFailure(Task task) =>
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        private static long ToStopwatchTicks(TimeSpan duration) =>
            checked((long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));
    }

    private sealed class CompositeLease : IOperationLease
    {
        private List<IOperationLease>? current;
        private readonly CancellationTokenSource leaseLostCancellation;

        public CompositeLease(List<IOperationLease> leases)
        {
            current = leases;
            leaseLostCancellation = leases.Count == 0
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(
                    leases.Select(lease => lease.LeaseLost).ToArray());
        }

        public CancellationToken LeaseLost => leaseLostCancellation.Token;

        public void ThrowIfLost()
        {
            foreach (var lease in current ?? [])
            {
                lease.ThrowIfLost();
            }
        }

        public async ValueTask DisposeAsync()
        {
            var released = Interlocked.Exchange(ref current, null);
            if (released is not null)
            {
                try
                {
                    await DisposeReverseAsync(released);
                }
                finally
                {
                    leaseLostCancellation.Dispose();
                }
            }
        }
    }
}
