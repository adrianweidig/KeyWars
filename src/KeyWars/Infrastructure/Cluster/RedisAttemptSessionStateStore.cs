using System.Text.Json;
using System.Text.Json.Serialization;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisAttemptSessionStateStore(IConnectionMultiplexer redis) : IAttemptSessionStateStore
{
    private const string Prefix = "keywars:{attempt}";
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly LuaScript AddScript = LuaScript.Prepare(
        "redis.call('set', @sessionKey, @value, 'PX', @ttlMilliseconds); " +
        "redis.call('sadd', @profileKey, @id); " +
        "redis.call('zadd', @expiryKey, @expiresAt, @id); " +
        "local profileTtl = redis.call('pttl', @profileKey); " +
        "if profileTtl < tonumber(@indexTtlMilliseconds) then " +
        "redis.call('pexpire', @profileKey, @indexTtlMilliseconds) end; " +
        "local expiryTtl = redis.call('pttl', @expiryKey); " +
        "if expiryTtl < tonumber(@indexTtlMilliseconds) then " +
        "redis.call('pexpire', @expiryKey, @indexTtlMilliseconds) end; return 1");
    private static readonly LuaScript CompareExchangeScript = LuaScript.Prepare(
        "if redis.call('get', @sessionKey) ~= @current then return 0 end; " +
        "redis.call('set', @sessionKey, @updated, 'PX', @ttlMilliseconds); " +
        "redis.call('sadd', @profileKey, @id); " +
        "redis.call('zadd', @expiryKey, @expiresAt, @id); " +
        "local profileTtl = redis.call('pttl', @profileKey); " +
        "if profileTtl < tonumber(@indexTtlMilliseconds) then " +
        "redis.call('pexpire', @profileKey, @indexTtlMilliseconds) end; " +
        "local expiryTtl = redis.call('pttl', @expiryKey); " +
        "if expiryTtl < tonumber(@indexTtlMilliseconds) then " +
        "redis.call('pexpire', @expiryKey, @indexTtlMilliseconds) end; return 1");
    private static readonly LuaScript RemoveScript = LuaScript.Prepare(
        "if redis.call('get', @sessionKey) ~= @current then return 0 end; " +
        "redis.call('del', @sessionKey); redis.call('srem', @profileKey, @id); " +
        "redis.call('zrem', @expiryKey, @id); return 1");
    private static readonly LuaScript RemoveProfileIndexEntryScript = LuaScript.Prepare(
        "if redis.call('exists', @sessionKey) == 1 then return 0 end; " +
        "redis.call('srem', @profileKey, @id); " +
        "redis.call('zrem', @expiryKey, @id); return 1");
    private static readonly LuaScript RemoveExpiryIndexEntryScript = LuaScript.Prepare(
        "if redis.call('exists', @sessionKey) == 1 then return 0 end; " +
        "redis.call('zrem', @expiryKey, @id); return 1");
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask AddAsync(
        AttemptSession session,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = GetExpiresAt(session, lifetime);
        var ttlMilliseconds = GetTtlMilliseconds(expiresAt);
        await database.ScriptEvaluateAsync(
            AddScript,
            new
            {
                sessionKey = SessionKey(session.Id),
                profileKey = ProfileKey(session.UserProfileId),
                expiryKey = ExpiryKey,
                value = Serialize(session),
                id = session.Id.ToString("N"),
                expiresAt = expiresAt.ToUnixTimeMilliseconds(),
                ttlMilliseconds,
                indexTtlMilliseconds = GetIndexTtlMilliseconds(ttlMilliseconds)
            });
    }

    public async ValueTask<AttemptSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await database.StringGetAsync(SessionKey(id));
        return value.IsNull ? null : Deserialize(value!);
    }

    public async ValueTask<bool> TryUpdateAsync(
        AttemptSession current,
        AttemptSession updated,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = GetExpiresAt(updated, lifetime);
        var ttlMilliseconds = GetTtlMilliseconds(expiresAt);
        var result = await database.ScriptEvaluateAsync(
            CompareExchangeScript,
            new
            {
                sessionKey = SessionKey(current.Id),
                profileKey = ProfileKey(current.UserProfileId),
                expiryKey = ExpiryKey,
                current = Serialize(current),
                updated = Serialize(updated),
                id = current.Id.ToString("N"),
                expiresAt = expiresAt.ToUnixTimeMilliseconds(),
                ttlMilliseconds,
                indexTtlMilliseconds = GetIndexTtlMilliseconds(ttlMilliseconds)
            });
        return (int)result == 1;
    }

    public async ValueTask<AttemptSession?> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        var result = await database.ScriptEvaluateAsync(
            RemoveScript,
            new
            {
                sessionKey = SessionKey(id),
                profileKey = ProfileKey(current.UserProfileId),
                expiryKey = ExpiryKey,
                current = Serialize(current),
                id = id.ToString("N")
            });
        return (int)result == 1 ? current : null;
    }

    public async ValueTask<IReadOnlyList<AttemptSession>> RemoveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var removed = new List<AttemptSession>();
        var profileKey = ProfileKey(profileId);
        for (var pass = 0; pass < 3; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await database.SetMembersAsync(profileKey);
            if (ids.Length == 0)
            {
                break;
            }

            foreach (var value in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(value.ToString(), "N", out var id))
                {
                    await database.SetRemoveAsync(profileKey, value);
                    continue;
                }

                if (await RemoveAsync(id, cancellationToken) is { } session)
                {
                    removed.Add(session);
                    continue;
                }

                await database.ScriptEvaluateAsync(
                    RemoveProfileIndexEntryScript,
                    new
                    {
                        sessionKey = SessionKey(id),
                        profileKey,
                        expiryKey = ExpiryKey,
                        id = id.ToString("N")
                    });
            }
        }

        return removed;
    }

    public async ValueTask<IOperationLease> AcquireLifecycleLockAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await RedisDistributedLease.AcquireAsync(database, LockKey(id), cancellationToken);

    public async ValueTask<IReadOnlyList<Guid>> GetExpiredIdsAsync(
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await database.SortedSetRangeByScoreAsync(
            ExpiryKey,
            stop: now.ToUnixTimeMilliseconds(),
            order: Order.Ascending,
            take: 100);
        return values
            .Select(value => Guid.TryParseExact(value.ToString(), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
    }

    public async ValueTask<AttemptSession?> TryRemoveExpiredAsync(
        Guid id,
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var session = await GetAsync(id, cancellationToken);
        if (session is null)
        {
            await database.ScriptEvaluateAsync(
                RemoveExpiryIndexEntryScript,
                new
                {
                    sessionKey = SessionKey(id),
                    expiryKey = ExpiryKey,
                    id = id.ToString("N")
                });
            return null;
        }

        if (now - (session.StartedAt ?? session.PreparedAt) <= lifetime)
        {
            return null;
        }

        return await RemoveAsync(id, cancellationToken);
    }

    private static RedisKey SessionKey(Guid id) => $"{Prefix}:session:{id:N}";
    private static RedisKey ProfileKey(Guid profileId) => $"{Prefix}:profile:{profileId:N}";
    private static RedisKey LockKey(Guid id) => $"{Prefix}:lock:{id:N}";
    private static RedisKey ExpiryKey => $"{Prefix}:expiry";

    private static DateTimeOffset GetExpiresAt(AttemptSession session, TimeSpan lifetime) =>
        (session.StartedAt ?? session.PreparedAt).Add(lifetime);

    private static long GetTtlMilliseconds(DateTimeOffset expiresAt)
    {
        var ttl = expiresAt - DateTimeOffset.UtcNow + ExpiryGrace;
        return (long)Math.Max(ExpiryGrace.TotalMilliseconds, ttl.TotalMilliseconds);
    }

    private static long GetIndexTtlMilliseconds(long sessionTtlMilliseconds) =>
        sessionTtlMilliseconds + (long)ExpiryGrace.TotalMilliseconds;

    private static string Serialize(AttemptSession session) =>
        JsonSerializer.Serialize(session, SerializerOptions);

    private static AttemptSession Deserialize(RedisValue value) =>
        JsonSerializer.Deserialize<AttemptSession>(value.ToString(), SerializerOptions)
        ?? throw new InvalidOperationException("Eine Redis-Versuchssitzung konnte nicht gelesen werden.");

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
