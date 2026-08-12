using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

internal sealed class RedisDistributedLease : IAsyncDisposable
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
    private readonly Task renewalTask;
    private int disposed;

    private RedisDistributedLease(
        IDatabase database,
        RedisKey key,
        RedisValue token,
        TimeSpan leaseDuration)
    {
        this.database = database;
        this.key = key;
        this.token = token;
        this.leaseDuration = leaseDuration;
        renewalTask = RenewUntilDisposedAsync();
    }

    public static async ValueTask<IAsyncDisposable> AcquireAsync(
        IDatabase database,
        RedisKey key,
        CancellationToken cancellationToken)
    {
        var leaseDuration = TimeSpan.FromSeconds(30);
        var token = Guid.NewGuid().ToString("N");
        while (!await database.StringSetAsync(key, token, leaseDuration, When.NotExists))
        {
            await Task.Delay(Random.Shared.Next(20, 75), cancellationToken);
        }

        return new RedisDistributedLease(database, key, token, leaseDuration);
    }

    public static async ValueTask<IAsyncDisposable?> TryAcquireAsync(
        IDatabase database,
        RedisKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leaseDuration = TimeSpan.FromSeconds(30);
        var token = Guid.NewGuid().ToString("N");
        return await database.StringSetAsync(key, token, leaseDuration, When.NotExists)
            ? new RedisDistributedLease(database, key, token, leaseDuration)
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await renewalCancellation.CancelAsync();
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
        }

        await database.ScriptEvaluateAsync(
            ReleaseScript,
            new { key, token });
        renewalCancellation.Dispose();
    }

    private async Task RenewUntilDisposedAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(renewalCancellation.Token))
        {
            var renewed = (int)await database.ScriptEvaluateAsync(
                RenewScript,
                new
                {
                    key,
                    token,
                    leaseMilliseconds = (long)leaseDuration.TotalMilliseconds
                });
            if (renewed == 0)
            {
                throw new InvalidOperationException($"Die verteilte Redis-Sperre {key} wurde verloren.");
            }
        }
    }
}
