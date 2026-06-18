using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record LivePresenceSwitch(Guid? PreviousRoomId, bool PreviousRoomLostLastConnection);
public sealed record LivePresenceLeave(Guid RoomId, Guid ProfileId, bool RoomLostLastConnection);

public sealed class LivePresenceTracker(IOptions<LiveOptions> options, TimeProvider timeProvider)
{
    private readonly object gate = new();
    private readonly Dictionary<string, LivePresenceConnection> byConnectionId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Dictionary<string, LivePresenceConnection>> byProfileId = [];

    public void EnsureCanConnect(Guid profileId, string connectionId)
    {
        lock (gate)
        {
            if (byConnectionId.ContainsKey(connectionId))
            {
                return;
            }

            var activeCount = byProfileId.TryGetValue(profileId, out var profileConnections)
                ? profileConnections.Count
                : 0;
            var limit = Math.Clamp(options.Value.MaxConnectionsPerUser, 1, 20);
            if (activeCount >= limit)
            {
                throw new InvalidOperationException($"Es sind maximal {limit} aktive Arena-Verbindungen pro Person erlaubt.");
            }
        }
    }

    public LivePresenceSwitch EnterRoom(Guid profileId, string connectionId, Guid roomId)
    {
        lock (gate)
        {
            EnsureCanConnect(profileId, connectionId);
            var now = timeProvider.GetUtcNow();
            if (byConnectionId.TryGetValue(connectionId, out var existing))
            {
                if (existing.ProfileId != profileId)
                {
                    throw new InvalidOperationException("Diese Arena-Verbindung gehört zu einer anderen Sitzung.");
                }

                if (existing.RoomId == roomId)
                {
                    existing.LastSeenAt = now;
                    return new LivePresenceSwitch(null, false);
                }

                RemoveConnectionUnlocked(connectionId, out var oldRoomId, out _, out var oldRoomLostLastConnection);
                AddConnectionUnlocked(new LivePresenceConnection(connectionId, profileId, roomId, now));
                return new LivePresenceSwitch(oldRoomId, oldRoomLostLastConnection);
            }

            AddConnectionUnlocked(new LivePresenceConnection(connectionId, profileId, roomId, now));
            return new LivePresenceSwitch(null, false);
        }
    }

    public LivePresenceLeave? LeaveRoom(Guid profileId, string connectionId, Guid roomId)
    {
        lock (gate)
        {
            if (!byConnectionId.TryGetValue(connectionId, out var connection) ||
                connection.ProfileId != profileId ||
                connection.RoomId != roomId)
            {
                return null;
            }

            RemoveConnectionUnlocked(connectionId, out var oldRoomId, out var oldProfileId, out var roomLostLastConnection);
            return oldRoomId is null || oldProfileId is null
                ? null
                : new LivePresenceLeave(oldRoomId.Value, oldProfileId.Value, roomLostLastConnection);
        }
    }

    public LivePresenceLeave? RemoveConnection(string connectionId)
    {
        lock (gate)
        {
            if (!byConnectionId.ContainsKey(connectionId))
            {
                return null;
            }

            RemoveConnectionUnlocked(connectionId, out var oldRoomId, out var oldProfileId, out var roomLostLastConnection);
            return oldRoomId is null || oldProfileId is null
                ? null
                : new LivePresenceLeave(oldRoomId.Value, oldProfileId.Value, roomLostLastConnection);
        }
    }

    public int CountRoomConnections(Guid profileId, Guid roomId)
    {
        lock (gate)
        {
            return byProfileId.TryGetValue(profileId, out var profileConnections)
                ? profileConnections.Values.Count(item => item.RoomId == roomId)
                : 0;
        }
    }

    private void AddConnectionUnlocked(LivePresenceConnection connection)
    {
        byConnectionId[connection.ConnectionId] = connection;
        if (!byProfileId.TryGetValue(connection.ProfileId, out var profileConnections))
        {
            profileConnections = [];
            byProfileId[connection.ProfileId] = profileConnections;
        }

        profileConnections[connection.ConnectionId] = connection;
    }

    private void RemoveConnectionUnlocked(
        string connectionId,
        out Guid? roomId,
        out Guid? profileId,
        out bool roomLostLastConnection)
    {
        roomId = null;
        profileId = null;
        roomLostLastConnection = false;
        if (!byConnectionId.Remove(connectionId, out var connection))
        {
            return;
        }

        roomId = connection.RoomId;
        profileId = connection.ProfileId;
        if (!byProfileId.TryGetValue(connection.ProfileId, out var profileConnections))
        {
            return;
        }

        profileConnections.Remove(connectionId);
        roomLostLastConnection = !profileConnections.Values.Any(item => item.RoomId == connection.RoomId);
        if (profileConnections.Count == 0)
        {
            byProfileId.Remove(connection.ProfileId);
        }
    }

    private sealed class LivePresenceConnection(string connectionId, Guid profileId, Guid roomId, DateTimeOffset lastSeenAt)
    {
        public string ConnectionId { get; } = connectionId;
        public Guid ProfileId { get; } = profileId;
        public Guid RoomId { get; } = roomId;
        public DateTimeOffset LastSeenAt { get; set; } = lastSeenAt;
    }
}
