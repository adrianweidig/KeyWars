using System.Collections.Concurrent;
using KeyWars.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Services;

public interface ILiveRoomUpdateSender
{
    Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken);

    void RemoveRoom(Guid roomId)
    {
    }
}

public sealed class SignalRLiveRoomUpdateSender(IHubContext<ArenaHub> hubContext) : ILiveRoomUpdateSender
{
    private readonly ConcurrentDictionary<Guid, RoomSendState> rooms = new();

    public async Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken)
    {
        var room = rooms.GetOrAdd(snapshot.RoomId, _ => new RoomSendState());
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

    public void RemoveRoom(Guid roomId) => rooms.TryRemove(roomId, out _);

    private sealed class RoomSendState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastSentStateVersion { get; set; }
    }
}
