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
    TimeProvider timeProvider) : ILivePresenceStateStore
{
    private const string Prefix = "keywars:{presence}";
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask EnsureCanConnectAsync(
        Guid profileId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var connection = await ReadConnectionAsync(connectionId);
        if (connection is not null)
        {
            if (connection.ProfileId != profileId)
            {
                throw new InvalidOperationException("Diese Arena-Verbindung gehört zu einer anderen Sitzung.");
            }

            presenceLock.ThrowIfLost();
            return;
        }

        var active = await ReadActiveConnectionsAsync(profileId);
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
        var existing = await ReadConnectionAsync(connectionId);
        if (existing is not null && existing.ProfileId != profileId)
        {
            throw new InvalidOperationException("Diese Arena-Verbindung gehört zu einer anderen Sitzung.");
        }

        var active = await ReadActiveConnectionsAsync(profileId);
        var limit = Math.Clamp(options.Value.MaxConnectionsPerUser, 1, 20);
        if (existing is null && active.Count >= limit)
        {
            throw new InvalidOperationException($"Es sind maximal {limit} aktive Arena-Verbindungen pro Person erlaubt.");
        }

        var changed = existing is null || existing.RoomId != roomId;
        var previousRoomLostLastConnection = existing is not null && existing.RoomId != roomId &&
            active.All(item => item.ConnectionId == connectionId || item.RoomId != existing.RoomId);
        var current = new PresenceConnection(connectionId, profileId, roomId, timeProvider.GetUtcNow());
        await database.StringSetAsync(ConnectionKey(connectionId), Serialize(current), ConnectionLifetime);
        await database.SetAddAsync(ProfileKey(profileId), ConnectionKey(connectionId).ToString());
        presenceLock.ThrowIfLost();
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
        var current = await ReadConnectionAsync(connectionId);
        if (current is null || current.ProfileId != profileId || current.RoomId != roomId)
        {
            return;
        }

        if (transition.PreviousRoomId is { } previousRoomId)
        {
            var restored = current with { RoomId = previousRoomId, LastSeenAt = timeProvider.GetUtcNow() };
            await database.StringSetAsync(ConnectionKey(connectionId), Serialize(restored), ConnectionLifetime);
            presenceLock.ThrowIfLost();
            return;
        }

        await DeleteConnectionAsync(current);
        presenceLock.ThrowIfLost();
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
        var current = await ReadConnectionAsync(connectionId);
        if (current is null || current.ProfileId != profileId || current.RoomId != roomId)
        {
            return null;
        }

        await DeleteConnectionAsync(current);
        var active = await ReadActiveConnectionsAsync(profileId);
        presenceLock.ThrowIfLost();
        return new LivePresenceLeave(roomId, profileId, active.All(item => item.RoomId != roomId));
    }

    public async ValueTask<LivePresenceLeave?> RemoveConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var observed = await ReadConnectionAsync(connectionId);
        if (observed is null)
        {
            return null;
        }

        await using var presenceLock = await AcquireProfileLockAsync(observed.ProfileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var current = await ReadConnectionAsync(connectionId);
        if (current is null)
        {
            return null;
        }

        await DeleteConnectionAsync(current);
        var active = await ReadActiveConnectionsAsync(current.ProfileId);
        presenceLock.ThrowIfLost();
        return new LivePresenceLeave(
            current.RoomId,
            current.ProfileId,
            active.All(item => item.RoomId != current.RoomId));
    }

    public async ValueTask<int> CountRoomConnectionsAsync(
        Guid profileId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var active = await ReadActiveConnectionsAsync(profileId);
        presenceLock.ThrowIfLost();
        return active.Count(item => item.RoomId == roomId);
    }

    public async ValueTask<IReadOnlyList<string>> RemoveProfileFromRoomAsync(
        Guid profileId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var presenceLock = await AcquireProfileLockAsync(profileId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, presenceLock);
        cancellationToken = operationCancellation.Token;
        var active = await ReadActiveConnectionsAsync(profileId);
        var removed = active.Where(item => item.RoomId == roomId).ToArray();
        foreach (var connection in removed)
        {
            await DeleteConnectionAsync(connection);
        }

        presenceLock.ThrowIfLost();

        return removed.Select(item => item.ConnectionId).ToArray();
    }

    private async ValueTask<IOperationLease> AcquireProfileLockAsync(Guid profileId, CancellationToken cancellationToken) =>
        await RedisDistributedLease.AcquireAsync(database, $"{Prefix}:lock:{profileId:N}", cancellationToken);

    private static CancellationTokenSource LinkToLease(
        CancellationToken cancellationToken,
        IOperationLease lease) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LeaseLost);

    private async Task<List<PresenceConnection>> ReadActiveConnectionsAsync(Guid profileId)
    {
        var profileKey = ProfileKey(profileId);
        var members = await database.SetMembersAsync(profileKey);
        var active = new List<PresenceConnection>(members.Length);
        foreach (var member in members)
        {
            var value = await database.StringGetAsync(member.ToString());
            if (value.IsNull)
            {
                await database.SetRemoveAsync(profileKey, member);
                continue;
            }

            var connection = Deserialize(value!);
            if (connection.ProfileId == profileId)
            {
                active.Add(connection);
            }
            else
            {
                await database.SetRemoveAsync(profileKey, member);
            }
        }

        return active;
    }

    private async Task<PresenceConnection?> ReadConnectionAsync(string connectionId)
    {
        var value = await database.StringGetAsync(ConnectionKey(connectionId));
        return value.IsNull ? null : Deserialize(value!);
    }

    private async Task DeleteConnectionAsync(PresenceConnection connection)
    {
        await database.KeyDeleteAsync(ConnectionKey(connection.ConnectionId));
        await database.SetRemoveAsync(ProfileKey(connection.ProfileId), ConnectionKey(connection.ConnectionId).ToString());
    }

    private static RedisKey ConnectionKey(string connectionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId));
        return $"{Prefix}:connection:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static RedisKey ProfileKey(Guid profileId) => $"{Prefix}:profile:{profileId:N}";

    private static string Serialize(PresenceConnection connection) =>
        JsonSerializer.Serialize(connection, SerializerOptions);

    private static PresenceConnection Deserialize(RedisValue value) =>
        JsonSerializer.Deserialize<PresenceConnection>(value.ToString(), SerializerOptions)
        ?? throw new InvalidOperationException("Ein Redis-Presence-Eintrag konnte nicht gelesen werden.");

    private sealed record PresenceConnection(
        string ConnectionId,
        Guid ProfileId,
        Guid RoomId,
        DateTimeOffset LastSeenAt);
}
