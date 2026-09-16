using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisLivePresenceStateStore(
    IConnectionMultiplexer redis,
    IOptions<LiveOptions> options,
    TimeProvider timeProvider) : ILivePresenceStateStore, ILivePresenceExpiryStore
{
    private const int MaximumProfileIndexSize = 1024;
    private const int CleanupBatchSize = 64;
    private const int ExpiryCleanupMaxPasses = 4;
    internal static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProfileIndexLifetime = TimeSpan.FromMinutes(4);
    internal static readonly TimeSpan ExpiryVisibilityTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly LuaScript WriteConnectionScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if @previousExpiryMember ~= '' then redis.call('zrem', @expiryKey, @previousExpiryMember); end; " +
        "redis.call('set', @connectionKey, @payload, 'PX', @ttlMilliseconds); " +
        "redis.call('sadd', @profileKey, @connectionKey); " +
        "redis.call('zadd', @expiryKey, @expiresAt, @connectionMember); " +
        "local profileTtl = redis.call('pttl', @profileKey); " +
        "if profileTtl < tonumber(@indexTtlMilliseconds) then " +
        "redis.call('pexpire', @profileKey, @indexTtlMilliseconds) end; return 1");
    private static readonly LuaScript DeleteConnectionScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('del', @connectionKey); redis.call('srem', @profileKey, @connectionKey); " +
        "redis.call('zrem', @expiryKey, @connectionMember); " +
        "if redis.call('scard', @profileKey) == 0 then redis.call('del', @profileKey) end; return 1");
    private static readonly LuaScript RemoveProfileMemberScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('srem', @profileKey, @connectionMember); return 1");
    private static readonly LuaScript RemoveExpiryMembersScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -1 end; " +
        "local members = cjson.decode(@members); local removed = 0; " +
        "for _, member in ipairs(members) do removed = removed + redis.call('zrem', @expiryKey, member); end; " +
        "return removed");
    private static readonly LuaScript ClaimExpiredConnectionsScript = LuaScript.Prepare(
        "local values = redis.call('zrangebyscore', @expiryKey, '-inf', @now, 'LIMIT', 0, @take); " +
        "for _, value in ipairs(values) do redis.call('zadd', @expiryKey, @claimUntil, value); end; return values");
    private static readonly LuaScript CompleteExpiryClaimScript = LuaScript.Prepare(
        "local payload = redis.call('get', @connectionKey); if payload then return {0, payload} end; " +
        "redis.call('srem', @profileKey, @connectionKey); redis.call('zrem', @expiryKey, @connectionMember); " +
        "if redis.call('scard', @profileKey) == 0 then redis.call('del', @profileKey) end; return {1, false}");
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask EnsureCanConnectAsync(
        Guid profileId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        if (await ReadConnectionAsync(profileId, connectionId, presenceLock, cancellationToken) is not null)
        {
            presenceLock.ThrowIfLost();
            return;
        }

        var active = await ReadActiveConnectionsAsync(profileId, presenceLock, cancellationToken);
        var limit = Math.Clamp(options.Value.MaxConnectionsPerUser, 1, 20);
        if (active.Count >= limit)
        {
            throw new InvalidOperationException($"Es sind maximal {limit} aktive Arena-Verbindungen pro Person erlaubt.");
        }

        presenceLock.ThrowIfLost();
    }

    public async ValueTask<LivePresenceSwitch> EnterRoomAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var existing = await ReadConnectionAsync(profileId, connectionId, presenceLock, cancellationToken);
        var active = await ReadActiveConnectionsAsync(profileId, presenceLock, cancellationToken);
        var limit = Math.Clamp(options.Value.MaxConnectionsPerUser, 1, 20);
        if (existing is null && active.Count >= limit)
        {
            throw new InvalidOperationException($"Es sind maximal {limit} aktive Arena-Verbindungen pro Person erlaubt.");
        }

        var changed = existing is null || existing.RoomId != roomId;
        var previousRoomLostLastConnection = existing is not null && existing.RoomId != roomId &&
            active.All(item => item.ConnectionId == connectionId || item.RoomId != existing.RoomId);
        var current = new PresenceConnection(connectionId, profileId, roomId, timeProvider.GetUtcNow());
        await StoreConnectionAsync(current, presenceLock, cancellationToken, existing);
        return new LivePresenceSwitch(changed ? existing?.RoomId : null, previousRoomLostLastConnection)
        {
            Changed = changed
        };
    }

    public async ValueTask RollbackEnterRoomAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        LivePresenceSwitch transition,
        CancellationToken cancellationToken = default)
    {
        if (!transition.Changed)
        {
            return;
        }

        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var current = await ReadConnectionAsync(profileId, connectionId, presenceLock, cancellationToken);
        if (current is null || current.RoomId != roomId)
        {
            return;
        }

        if (transition.PreviousRoomId is { } previousRoomId)
        {
            await StoreConnectionAsync(
                current with { RoomId = previousRoomId, LastSeenAt = timeProvider.GetUtcNow() },
                presenceLock,
                cancellationToken,
                current);
            return;
        }

        await DeleteConnectionAsync(current, presenceLock, cancellationToken);
    }

    public async ValueTask<LivePresenceLeave?> LeaveRoomAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var current = await ReadConnectionAsync(profileId, connectionId, presenceLock, cancellationToken);
        if (current is null || current.RoomId != roomId)
        {
            return null;
        }

        await DeleteConnectionAsync(current, presenceLock, cancellationToken);
        var active = await ReadActiveConnectionsAsync(profileId, presenceLock, cancellationToken);
        return new LivePresenceLeave(roomId, profileId, active.All(item => item.RoomId != roomId));
    }

    public async ValueTask<LivePresenceLeave?> RemoveConnectionAsync(
        Guid profileId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        var operationToken = operationCancellation.Token;
        var current = await ReadConnectionAsync(profileId, connectionId, presenceLock, operationToken);
        if (current is null)
        {
            return null;
        }

        await DeleteConnectionAsync(current, presenceLock, operationToken);
        var active = await ReadActiveConnectionsAsync(profileId, presenceLock, operationToken);
        return new LivePresenceLeave(
            current.RoomId,
            profileId,
            active.All(item => item.RoomId != current.RoomId));
    }

    public async ValueTask<int> CountRoomConnectionsAsync(
        Guid profileId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        var active = await ReadActiveConnectionsAsync(profileId, presenceLock, operationCancellation.Token);
        presenceLock.ThrowIfLost();
        return active.Count(item => item.RoomId == roomId);
    }

    public async ValueTask<bool> RefreshConnectionAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var current = await ReadConnectionAsync(profileId, connectionId, presenceLock, cancellationToken);
        if (current is null || current.RoomId != roomId)
        {
            return false;
        }

        await StoreConnectionAsync(
            current with { LastSeenAt = timeProvider.GetUtcNow() },
            presenceLock,
            cancellationToken,
            current);
        return true;
    }

    public async ValueTask<IReadOnlyList<string>> RemoveProfileFromRoomAsync(
        Guid profileId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        var operationToken = operationCancellation.Token;
        var active = await ReadActiveConnectionsAsync(profileId, presenceLock, operationToken);
        var removed = active.Where(item => item.RoomId == roomId).ToArray();
        foreach (var connection in removed)
        {
            await DeleteConnectionAsync(connection, presenceLock, operationToken);
        }

        presenceLock.ThrowIfLost();
        return removed.Select(item => item.ConnectionId).ToArray();
    }

    public async ValueTask RemoveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        var operationToken = operationCancellation.Token;
        var profileKey = ProfileKey(profileId);
        while (true)
        {
            var members = await ReadProfileMemberBatchAsync(profileKey, operationToken);
            if (members.Count == 0)
            {
                break;
            }

            foreach (var member in members)
            {
                await DeleteProfileMemberAsync(profileId, member, presenceLock, operationToken);
            }
        }

        await PurgeProfileExpiryMembersAsync(profileId, presenceLock, operationToken);
        presenceLock.ThrowIfLost();
    }

    private async ValueTask<RedisDistributedLease> AcquireProfileLockAsync(
        Guid profileId,
        CancellationToken cancellationToken) =>
        await RedisDistributedLease.AcquireAsync(database, LockKey(profileId), cancellationToken);

    private static CancellationTokenSource LinkToLease(
        CancellationToken cancellationToken,
        IOperationLease lease) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LeaseLost);

    private async Task StoreConnectionAsync(
        PresenceConnection connection,
        RedisDistributedLease presenceLock,
        CancellationToken cancellationToken,
        PresenceConnection? previous = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = (int)await database.ScriptEvaluateAsync(
            WriteConnectionScript,
            new
            {
                lockKey = presenceLock.Key,
                lockToken = presenceLock.Token,
                connectionKey = ConnectionKey(connection.ProfileId, connection.ConnectionId),
                profileKey = ProfileKey(connection.ProfileId),
                expiryKey = ExpiryKey(connection.ProfileId),
                connectionMember = SerializeExpiryMember(connection),
                previousExpiryMember = previous is null ? "" : SerializeExpiryMember(previous),
                payload = Serialize(connection),
                expiresAt = timeProvider.GetUtcNow().Add(ConnectionLifetime).ToUnixTimeMilliseconds(),
                ttlMilliseconds = (long)ConnectionLifetime.TotalMilliseconds,
                indexTtlMilliseconds = (long)ProfileIndexLifetime.TotalMilliseconds
            });
        if (stored == 0)
        {
            presenceLock.ThrowFenceLost("Presence-Verbindung speichern");
        }

        presenceLock.ThrowIfLost();
    }

    private async Task<List<PresenceConnection>> ReadActiveConnectionsAsync(
        Guid profileId,
        RedisDistributedLease presenceLock,
        CancellationToken cancellationToken)
    {
        var profileKey = ProfileKey(profileId);
        var members = await SnapshotProfileMembersAsync(profileKey, cancellationToken);
        var active = new List<PresenceConnection>();
        var stale = new List<(RedisKey Key, RedisValue Payload)>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await database.StringGetAsync(member.ToString());
            if (value.IsNull || !TryDeserializeConnection(value!, out var connection) ||
                connection.ProfileId != profileId ||
                ConnectionKey(profileId, connection.ConnectionId).ToString() != member.ToString())
            {
                stale.Add((member.ToString(), value));
            }
            else
            {
                active.Add(connection);
            }
        }

        foreach (var batch in stale.Chunk(CleanupBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(batch.Select(member =>
                DeleteProfileMemberAsync(
                    profileId,
                    member.Key,
                    presenceLock,
                    cancellationToken,
                    member.Payload)));
        }

        presenceLock.ThrowIfLost();
        return active;
    }

    private async Task<RedisValue[]> SnapshotProfileMembersAsync(
        RedisKey profileKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var countBefore = await database.SetLengthAsync(profileKey);
            EnsureProfileIndexIsBounded(countBefore);
            var members = await database.SetMembersAsync(profileKey);
            EnsureProfileIndexIsBounded(members.LongLength);
            var countAfter = await database.SetLengthAsync(profileKey);
            EnsureProfileIndexIsBounded(countAfter);
            if (countBefore == members.LongLength && countAfter == countBefore)
            {
                return members;
            }
        }

        throw new InvalidOperationException(
            "Der verteilte Presence-Index hat sich während des geschützten Lesevorgangs verändert.");
    }

    private static void EnsureProfileIndexIsBounded(long memberCount)
    {
        if (memberCount > MaximumProfileIndexSize)
        {
            throw new InvalidOperationException(
                "Der verteilte Presence-Index ist ungewöhnlich groß und muss vor einer weiteren Verbindung bereinigt werden.");
        }
    }

    private async Task<IReadOnlyList<RedisKey>> ReadProfileMemberBatchAsync(
        RedisKey profileKey,
        CancellationToken cancellationToken)
    {
        var members = new List<RedisKey>(CleanupBatchSize);
        await foreach (var member in database.SetScanAsync(
            profileKey,
            pattern: default,
            pageSize: CleanupBatchSize).WithCancellation(cancellationToken))
        {
            members.Add(member.ToString());
            if (members.Count == CleanupBatchSize)
            {
                break;
            }
        }

        return members;
    }

    private async Task<PresenceConnection?> ReadConnectionAsync(
        Guid profileId,
        string connectionId,
        RedisDistributedLease presenceLock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connectionKey = ConnectionKey(profileId, connectionId);
        var value = await database.StringGetAsync(connectionKey);
        if (value.IsNull)
        {
            return null;
        }

        if (!TryDeserializeConnection(value!, out var connection) ||
            connection.ProfileId != profileId ||
            !string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            await DeleteProfileMemberAsync(
                profileId,
                connectionKey,
                presenceLock,
                cancellationToken,
                value);
            return null;
        }

        return connection;
    }

    private async Task DeleteConnectionAsync(
        PresenceConnection connection,
        RedisDistributedLease presenceLock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = (int)await database.ScriptEvaluateAsync(
            DeleteConnectionScript,
            new
            {
                lockKey = presenceLock.Key,
                lockToken = presenceLock.Token,
                connectionKey = ConnectionKey(connection.ProfileId, connection.ConnectionId),
                profileKey = ProfileKey(connection.ProfileId),
                expiryKey = ExpiryKey(connection.ProfileId),
                connectionMember = SerializeExpiryMember(connection)
            });
        if (deleted == 0)
        {
            presenceLock.ThrowFenceLost("Presence-Verbindung entfernen");
        }

        presenceLock.ThrowIfLost();
    }

    private async Task DeleteProfileMemberAsync(
        Guid profileId,
        RedisKey connectionKey,
        RedisDistributedLease presenceLock,
        CancellationToken cancellationToken,
        RedisValue? observedPayload = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileKey = ProfileKey(profileId);
        var payload = observedPayload ?? (HasSameHashTag(profileKey, connectionKey)
            ? await database.StringGetAsync(connectionKey)
            : RedisValue.Null);
        var expiryMember = !payload.IsNull &&
            TryDeserializeConnection(payload, out var connection) &&
            connection.ProfileId == profileId &&
            ConnectionKey(profileId, connection.ConnectionId).ToString() == connectionKey.ToString()
                ? SerializeExpiryMember(connection)
                : "";
        var deleted = HasSameHashTag(profileKey, connectionKey)
            ? (int)await database.ScriptEvaluateAsync(
                DeleteConnectionScript,
                new
                {
                    lockKey = presenceLock.Key,
                    lockToken = presenceLock.Token,
                    connectionKey,
                    profileKey,
                    expiryKey = ExpiryKey(profileId),
                    connectionMember = expiryMember
                })
            : (int)await database.ScriptEvaluateAsync(
                RemoveProfileMemberScript,
                new
                {
                    lockKey = presenceLock.Key,
                    lockToken = presenceLock.Token,
                    connectionMember = (RedisValue)connectionKey.ToString(),
                    profileKey
                });
        if (deleted == 0)
        {
            presenceLock.ThrowFenceLost("Presence-Verbindung entfernen");
        }

        presenceLock.ThrowIfLost();
    }

    private async Task PurgeProfileExpiryMembersAsync(
        Guid profileId,
        RedisDistributedLease presenceLock,
        CancellationToken cancellationToken)
    {
        var expiryKey = ExpiryKey(profileId);
        for (var pass = 0; pass < ExpiryCleanupMaxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<RedisValue>(CleanupBatchSize);
            var found = false;
            await foreach (var entry in database.SortedSetScanAsync(
                expiryKey,
                pattern: default,
                pageSize: CleanupBatchSize).WithCancellation(cancellationToken))
            {
                if (!TryDeserializeExpiryMember(entry.Element.ToString(), out var member) ||
                    member.ProfileId != profileId)
                {
                    continue;
                }

                found = true;
                matches.Add(entry.Element);
                if (matches.Count == CleanupBatchSize)
                {
                    await RemoveExpiryMembersAsync(expiryKey, matches, presenceLock);
                    matches.Clear();
                }
            }

            if (matches.Count > 0)
            {
                await RemoveExpiryMembersAsync(expiryKey, matches, presenceLock);
            }
            if (!found)
            {
                presenceLock.ThrowIfLost();
                return;
            }
        }

        throw new InvalidOperationException(
            "Die Presence-Profilbereinigung konnte keinen stabilen Ablaufindex erreichen.");
    }

    private async Task RemoveExpiryMembersAsync(
        RedisKey expiryKey,
        IReadOnlyList<RedisValue> members,
        RedisDistributedLease presenceLock)
    {
        var removed = (int)await database.ScriptEvaluateAsync(
            RemoveExpiryMembersScript,
            new
            {
                lockKey = presenceLock.Key,
                lockToken = presenceLock.Token,
                expiryKey,
                members = JsonSerializer.Serialize(members.Select(member => member.ToString()))
            });
        if (removed < 0)
        {
            presenceLock.ThrowFenceLost("Presence-Ablaufindex bereinigen");
        }

        presenceLock.ThrowIfLost();
    }

    internal static RedisKey ConnectionKey(Guid profileId, string connectionId) =>
        $"{PresencePrefix(profileId)}:connection:{ConnectionHash(connectionId)}";

    internal static RedisKey ProfileKey(Guid profileId) =>
        $"{PresencePrefix(profileId)}:profile:{profileId:N}";

    internal static RedisKey LockKey(Guid profileId) =>
        $"{PresencePrefix(profileId)}:lock:{profileId:N}";

    internal static RedisKey ExpiryKey(Guid profileId) =>
        $"{PresencePrefix(profileId)}:expiry";

    async Task<PresenceExpiryClaimBatch> ILivePresenceExpiryStore.ClaimExpiredAsync(
        byte bucket,
        int take,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var claimed = (RedisResult[]?)await database.ScriptEvaluateAsync(
            ClaimExpiredConnectionsScript,
            new
            {
                expiryKey = ExpiryKey(bucket),
                now = now.ToUnixTimeMilliseconds(),
                claimUntil = now.Add(ExpiryVisibilityTimeout).ToUnixTimeMilliseconds(),
                take = Math.Clamp(take, 1, 64)
            }) ?? [];
        var valid = new List<ExpiredPresenceClaim>(claimed.Length);
        var invalid = new List<RedisValue>();
        foreach (var value in claimed)
        {
            var raw = value.ToString();
            if (TryDeserializeExpiryMember(raw, out var member) &&
                RedisClusterKeyspace.GetBucket(member.ProfileId) == bucket)
            {
                valid.Add(member);
            }
            else
            {
                invalid.Add(raw);
            }
        }

        if (invalid.Count > 0)
        {
            await database.SortedSetRemoveAsync(ExpiryKey(bucket), invalid.ToArray());
        }

        return new PresenceExpiryClaimBatch(valid, invalid.Count);
    }

    async Task<bool> ILivePresenceExpiryStore.CompleteExpiryClaimAsync(
        ExpiredPresenceClaim claim,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (RedisResult[]?)await database.ScriptEvaluateAsync(
            CompleteExpiryClaimScript,
            new
            {
                connectionKey = ConnectionKey(claim.ProfileId, claim.ConnectionId),
                profileKey = ProfileKey(claim.ProfileId),
                expiryKey = ExpiryKey(claim.ProfileId),
                connectionMember = SerializeExpiryMember(claim)
            });
        return result is { Length: > 0 } && (int)result[0] == 1;
    }

    async Task<bool> ILivePresenceExpiryStore.ExecuteIfRoomEmptyAsync(
        ExpiredPresenceClaim claim,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await using var presenceLock = await AcquireProfileLockAsync(claim.ProfileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        var operationToken = operationCancellation.Token;
        var active = await ReadActiveConnectionsAsync(claim.ProfileId, presenceLock, operationToken);
        if (active.Any(connection => connection.RoomId == claim.RoomId))
        {
            presenceLock.ThrowIfLost();
            return false;
        }

        await action(operationToken);
        presenceLock.ThrowIfLost();
        return true;
    }

    async Task ILivePresenceExpiryStore.ReleaseExpiryClaimAsync(ExpiredPresenceClaim claim)
    {
        await database.SortedSetAddAsync(
            ExpiryKey(claim.ProfileId),
            SerializeExpiryMember(claim),
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private static RedisKey ExpiryKey(byte bucket) =>
        $"keywars:{{presence-b{bucket:x2}}}:expiry";

    private static string PresencePrefix(Guid profileId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag("presence", profileId)}";

    private static string ConnectionHash(string connectionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasSameHashTag(RedisKey first, RedisKey second) =>
        string.Equals(HashTag(first), HashTag(second), StringComparison.Ordinal);

    private static string HashTag(RedisKey key)
    {
        var value = key.ToString();
        var start = value.IndexOf('{');
        var end = value.IndexOf('}', start + 1);
        return start >= 0 && end > start + 1 ? value[(start + 1)..end] : value;
    }

    private static string Serialize(PresenceConnection connection) =>
        JsonSerializer.Serialize(connection, SerializerOptions);

    private static string SerializeExpiryMember(PresenceConnection connection) =>
        SerializeExpiryMember(new ExpiredPresenceClaim(
            connection.ConnectionId,
            connection.ProfileId,
            connection.RoomId));

    private static string SerializeExpiryMember(ExpiredPresenceClaim connection) =>
        JsonSerializer.Serialize(connection, SerializerOptions);

    private static bool TryDeserializeExpiryMember(string value, out ExpiredPresenceClaim connection)
    {
        try
        {
            connection = JsonSerializer.Deserialize<ExpiredPresenceClaim>(value, SerializerOptions)!;
            return connection is not null && !string.IsNullOrEmpty(connection.ConnectionId);
        }
        catch (JsonException)
        {
            connection = null!;
            return false;
        }
    }

    private static bool TryDeserializeConnection(RedisValue value, out PresenceConnection connection)
    {
        try
        {
            connection = JsonSerializer.Deserialize<PresenceConnection>(value.ToString(), SerializerOptions)!;
            return connection is not null && !string.IsNullOrEmpty(connection.ConnectionId);
        }
        catch (JsonException)
        {
            connection = null!;
            return false;
        }
    }

    private sealed record PresenceConnection(
        string ConnectionId,
        Guid ProfileId,
        Guid RoomId,
        DateTimeOffset LastSeenAt);
}

internal sealed record ExpiredPresenceClaim(string ConnectionId, Guid ProfileId, Guid RoomId);

internal sealed record PresenceExpiryClaimBatch(
    IReadOnlyList<ExpiredPresenceClaim> Claims,
    int InvalidMembers);

internal interface ILivePresenceExpiryStore
{
    Task<PresenceExpiryClaimBatch> ClaimExpiredAsync(
        byte bucket,
        int take,
        CancellationToken cancellationToken);
    Task<bool> CompleteExpiryClaimAsync(
        ExpiredPresenceClaim claim,
        CancellationToken cancellationToken);
    Task<bool> ExecuteIfRoomEmptyAsync(
        ExpiredPresenceClaim claim,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken);
    Task ReleaseExpiryClaimAsync(ExpiredPresenceClaim claim);
}
