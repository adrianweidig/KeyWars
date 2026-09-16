using System.Text.Json;
using System.Text.Json.Serialization;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisAttemptSessionStateStore(IConnectionMultiplexer redis) : IAttemptSessionStateStore
{
    private const string Subsystem = "attempt";
    private const int BatchSize = 100;
    private const int ExpiryBucketsPerRead = 8;
    private const int ExpiryEntriesPerBucket = 13;
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
    private static readonly LuaScript RemoveForeignProfileIndexEntryScript = LuaScript.Prepare(
        "if redis.call('get', @sessionKey) == @current then " +
        "return redis.call('srem', @profileKey, @id) else return 0 end");
    private static readonly LuaScript RemoveProfileMemberScript = LuaScript.Prepare(
        "return redis.call('srem', @profileKey, @id)");
    private static readonly LuaScript RemoveExpiryIndexEntryScript = LuaScript.Prepare(
        "if redis.call('exists', @sessionKey) == 1 then return 0 end; " +
        "redis.call('zrem', @expiryKey, @id); return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private int expirySweepCursor = -ExpiryBucketsPerRead;

    public async ValueTask AddAsync(
        AttemptSession session,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = GetExpiresAt(session, lifetime);
        var ttlMilliseconds = GetTtlMilliseconds(expiresAt);
        var bucket = RedisClusterKeyspace.GetBucket(session.Id);
        await database.ScriptEvaluateAsync(
            AddScript,
            new
            {
                sessionKey = SessionKey(session.Id),
                profileKey = ProfileKey(session.UserProfileId, bucket),
                expiryKey = ExpiryKey(bucket),
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
        if (updated.Id != current.Id || updated.UserProfileId != current.UserProfileId)
        {
            throw new ArgumentException("Eine Versuchssitzung darf beim Aktualisieren weder ID noch Profil wechseln.", nameof(updated));
        }

        var expiresAt = GetExpiresAt(updated, lifetime);
        var ttlMilliseconds = GetTtlMilliseconds(expiresAt);
        var bucket = RedisClusterKeyspace.GetBucket(current.Id);
        var result = await database.ScriptEvaluateAsync(
            CompareExchangeScript,
            new
            {
                sessionKey = SessionKey(current.Id),
                profileKey = ProfileKey(current.UserProfileId, bucket),
                expiryKey = ExpiryKey(bucket),
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

        return await TryRemoveAsync(current) ? current : null;
    }

    private async ValueTask<bool> TryRemoveAsync(AttemptSession current)
    {
        var id = current.Id;
        var bucket = RedisClusterKeyspace.GetBucket(id);
        var result = await database.ScriptEvaluateAsync(
            RemoveScript,
            new
            {
                sessionKey = SessionKey(id),
                profileKey = ProfileKey(current.UserProfileId, bucket),
                expiryKey = ExpiryKey(bucket),
                current = Serialize(current),
                id = id.ToString("N")
            });
        return (int)result == 1;
    }

    public async ValueTask<IReadOnlyList<AttemptSession>> RemoveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var removed = new List<AttemptSession>();
        for (var bucketIndex = 0; bucketIndex < RedisClusterKeyspace.BucketCount; bucketIndex++)
        {
            var bucket = checked((byte)bucketIndex);
            var profileKey = ProfileKey(profileId, bucket);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = new List<RedisValue>(BatchSize);
                await foreach (var value in database
                    .SetScanAsync(profileKey, pageSize: BatchSize)
                    .WithCancellation(cancellationToken))
                {
                    ids.Add(value);
                    if (ids.Count == BatchSize)
                    {
                        break;
                    }
                }

                if (ids.Count == 0)
                {
                    break;
                }

                foreach (var value in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(value.ToString(), "N", out var id) ||
                        RedisClusterKeyspace.GetBucket(id) != bucket)
                    {
                        await RemoveProfileMembershipAsync(profileKey, value);
                        continue;
                    }

                    var session = await GetAsync(id, cancellationToken);
                    if (session is not null && session.UserProfileId != profileId)
                    {
                        await database.ScriptEvaluateAsync(
                            RemoveForeignProfileIndexEntryScript,
                            new
                            {
                                sessionKey = SessionKey(id),
                                profileKey,
                                current = Serialize(session),
                                id = value
                            });
                        continue;
                    }

                    if (session is not null && await TryRemoveAsync(session))
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
                            expiryKey = ExpiryKey(bucket),
                            id = id.ToString("N")
                        });
                }
            }
        }

        return removed;
    }

    private async ValueTask RemoveProfileMembershipAsync(RedisKey profileKey, RedisValue id) =>
        _ = await database.ScriptEvaluateAsync(
            RemoveProfileMemberScript,
            new { profileKey, id });

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
        var firstBucket = unchecked((uint)Interlocked.Add(ref expirySweepCursor, ExpiryBucketsPerRead));
        var buckets = Enumerable.Range(0, ExpiryBucketsPerRead)
            .Select(offset => unchecked((byte)(firstBucket + (uint)offset)))
            .ToArray();
        var reads = buckets
            .Select(bucket => database.SortedSetRangeByScoreWithScoresAsync(
                ExpiryKey(bucket),
                stop: now.ToUnixTimeMilliseconds(),
                order: Order.Ascending,
                take: ExpiryEntriesPerBucket))
            .ToArray();
        var entries = await Task.WhenAll(reads);
        cancellationToken.ThrowIfCancellationRequested();
        var repairedEntries = await Task.WhenAll(entries.Select((bucketEntries, index) =>
            ReadValidExpiryEntriesAsync(
                buckets[index],
                bucketEntries,
                now.ToUnixTimeMilliseconds(),
                cancellationToken)));
        return repairedEntries
            .SelectMany(bucketEntries => bucketEntries)
            .OrderBy(entry => entry.Score)
            .ThenBy(entry => entry.Id)
            .Take(BatchSize)
            .Select(entry => entry.Id)
            .ToArray();
    }

    private async Task<IReadOnlyList<ExpiryCandidate>> ReadValidExpiryEntriesAsync(
        byte bucket,
        SortedSetEntry[] entries,
        long stop,
        CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valid = new List<ExpiryCandidate>(entries.Length);
            var corrupt = new List<RedisValue>();
            foreach (var entry in entries)
            {
                if (Guid.TryParseExact(entry.Element.ToString(), "N", out var id) &&
                    RedisClusterKeyspace.GetBucket(id) == bucket)
                {
                    valid.Add(new ExpiryCandidate(id, entry.Score));
                }
                else
                {
                    corrupt.Add(entry.Element);
                }
            }

            if (corrupt.Count == 0)
            {
                return valid;
            }

            await database.SortedSetRemoveAsync(ExpiryKey(bucket), corrupt.ToArray());
            if (pass == 1)
            {
                return valid;
            }

            cancellationToken.ThrowIfCancellationRequested();
            entries = await database.SortedSetRangeByScoreWithScoresAsync(
                ExpiryKey(bucket),
                stop: stop,
                order: Order.Ascending,
                take: ExpiryEntriesPerBucket);
        }

        return [];
    }

    public async ValueTask<AttemptSession?> TryRemoveExpiredAsync(
        Guid id,
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var session = await GetAsync(id, cancellationToken);
        var bucket = RedisClusterKeyspace.GetBucket(id);
        if (session is null)
        {
            await database.ScriptEvaluateAsync(
                RemoveExpiryIndexEntryScript,
                new
                {
                    sessionKey = SessionKey(id),
                    expiryKey = ExpiryKey(bucket),
                    id = id.ToString("N")
                });
            return null;
        }

        if (now - (session.StartedAt ?? session.PreparedAt) <= lifetime)
        {
            return null;
        }

        return await TryRemoveAsync(session) ? session : null;
    }

    internal static RedisKey SessionKey(Guid id) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, id)}:session:{id:N}";

    internal static RedisKey ProfileKey(Guid profileId, byte bucket) =>
        $"keywars:{{{Subsystem}-b{bucket:x2}}}:profile:{profileId:N}";

    internal static RedisKey LockKey(Guid id) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, id)}:lock:{id:N}";

    internal static RedisKey ExpiryKey(byte bucket) =>
        $"keywars:{{{Subsystem}-b{bucket:x2}}}:expiry";

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

    private readonly record struct ExpiryCandidate(Guid Id, double Score);
}
