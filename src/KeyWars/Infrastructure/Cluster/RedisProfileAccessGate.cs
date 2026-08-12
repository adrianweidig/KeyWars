using System.Collections.Concurrent;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisProfileAccessGate(IConnectionMultiplexer redis) : IProfileAccessGate
{
    private const string Prefix = "keywars:profile-access";
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

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
            1 => new AccessLease(database, ActiveKey(profileId), token),
            -1 => throw new ProfileOperationException("profile_deleted", "Dieses Profil wurde bereits gelöscht."),
            _ => throw new ProfileOperationException(
                "profile_operation_in_progress",
                "Für dieses Profil läuft bereits eine Datenschutzoperation.")
        };
    }

    public async ValueTask<IAsyncDisposable> AcquireManyAsync(
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        var leases = new List<IAsyncDisposable>();
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

    public async ValueTask<bool> TryBeginOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = $"op:{Guid.NewGuid():N}";
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
            return false;
        }

        var operation = new OperationLease(database, profileId, token);
        if (operations.TryAdd(profileId, operation))
        {
            return true;
        }

        await operation.DisposeAsync();
        return false;
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

    private static async ValueTask DisposeReverseAsync(IReadOnlyList<IAsyncDisposable> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync();
        }
    }

    private sealed class AccessLease : IAsyncDisposable
    {
        private readonly IDatabase database;
        private readonly RedisKey activeKey;
        private readonly RedisValue token;
        private readonly CancellationTokenSource renewalCancellation = new();
        private readonly Task renewalTask;
        private int disposed;

        public AccessLease(IDatabase database, RedisKey activeKey, RedisValue token)
        {
            this.database = database;
            this.activeKey = activeKey;
            this.token = token;
            renewalTask = RenewAsync();
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

            await database.ScriptEvaluateAsync(ReleaseLeaseScript, new { activeKey, token });
            renewalCancellation.Dispose();
        }

        private async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(LeaseDuration / 3);
            while (await timer.WaitForNextTickAsync(renewalCancellation.Token))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var renewed = (int)await database.ScriptEvaluateAsync(
                    RenewLeaseScript,
                    new
                    {
                        activeKey,
                        token,
                        expiresAt = now + (long)LeaseDuration.TotalMilliseconds,
                        setLifetime = (long)(LeaseDuration.TotalMilliseconds * 4)
                    });
                if (renewed != 1)
                {
                    throw new InvalidOperationException("Der verteilte Profilzugriffs-Lease wurde verloren.");
                }
            }
        }
    }

    private sealed class OperationLease : IAsyncDisposable
    {
        private readonly IDatabase database;
        private readonly Guid profileId;
        private readonly RedisValue operationToken;
        private readonly CancellationTokenSource renewalCancellation = new();
        private readonly Task renewalTask;
        private int deleted;
        private int disposed;

        public OperationLease(IDatabase database, Guid profileId, RedisValue operationToken)
        {
            this.database = database;
            this.profileId = profileId;
            this.operationToken = operationToken;
            renewalTask = RenewAsync();
        }

        public async ValueTask MarkDeletedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            if (Volatile.Read(ref deleted) == 0)
            {
                await database.ScriptEvaluateAsync(
                    CompleteOperationScript,
                    new { stateKey = StateKey(profileId), operationToken });
            }

            renewalCancellation.Dispose();
        }

        private async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(OperationDuration / 3);
            while (await timer.WaitForNextTickAsync(renewalCancellation.Token))
            {
                if (Volatile.Read(ref deleted) != 0)
                {
                    return;
                }

                var renewed = (int)await database.ScriptEvaluateAsync(
                    RenewOperationScript,
                    new
                    {
                        stateKey = StateKey(profileId),
                        operationToken,
                        operationLifetime = (long)OperationDuration.TotalMilliseconds
                    });
                if (renewed != 1)
                {
                    throw new InvalidOperationException("Der exklusive Profilzugriff wurde verloren.");
                }
            }
        }
    }

    private sealed class CompositeLease(List<IAsyncDisposable> leases) : IAsyncDisposable
    {
        private List<IAsyncDisposable>? current = leases;

        public async ValueTask DisposeAsync()
        {
            var released = Interlocked.Exchange(ref current, null);
            if (released is not null)
            {
                await DisposeReverseAsync(released);
            }
        }
    }
}
