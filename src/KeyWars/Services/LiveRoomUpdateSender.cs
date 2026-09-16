using System.Collections.Concurrent;
using KeyWars.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public interface ILiveRoomUpdateSender
{
    Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken);

    void RemoveRoom(Guid roomId)
    {
    }
}

public sealed class SignalRLiveRoomUpdateSender(
    IHubContext<ArenaHub> hubContext,
    IOptions<LiveOptions> options) : ILiveRoomUpdateSender
{
    private readonly ConcurrentDictionary<Guid, RoomSendState> rooms = new();
    private readonly object roomIndexGate = new();
    private readonly LinkedList<Guid> roomOrder = new();
    private readonly int roomCapacity = Math.Clamp(options.Value.MaxConcurrentRooms, 1, 65_536);

    public async Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken)
    {
        var room = GetOrCreateRoom(snapshot.RoomId);
        await room.Gate.WaitAsync(CancellationToken.None);
        try
        {
            if (snapshot.StateVersion <= room.LastSentStateVersion)
            {
                return;
            }

            await hubContext.Clients
                .Group(snapshot.RoomId.ToString("N"))
                .SendAsync("roomChanged", snapshot, CancellationToken.None);
            room.LastSentStateVersion = snapshot.StateVersion;
        }
        finally
        {
            room.Gate.Release();
        }
    }

    public void RemoveRoom(Guid roomId)
    {
        lock (roomIndexGate)
        {
            if (rooms.TryRemove(roomId, out var room))
            {
                roomOrder.Remove(room.OrderNode);
            }
        }
    }

    internal int TrackedRoomCount => rooms.Count;

    private RoomSendState GetOrCreateRoom(Guid roomId)
    {
        if (rooms.TryGetValue(roomId, out var existing))
        {
            return existing;
        }

        lock (roomIndexGate)
        {
            if (rooms.TryGetValue(roomId, out existing))
            {
                return existing;
            }

            while (rooms.Count >= roomCapacity && roomOrder.First is { } oldest)
            {
                roomOrder.RemoveFirst();
                rooms.TryRemove(oldest.Value, out _);
            }

            var node = roomOrder.AddLast(roomId);
            var created = new RoomSendState(node);
            rooms[roomId] = created;
            return created;
        }
    }

    private sealed class RoomSendState(LinkedListNode<Guid> orderNode)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public LinkedListNode<Guid> OrderNode { get; } = orderNode;
        public long LastSentStateVersion { get; set; }
    }
}
