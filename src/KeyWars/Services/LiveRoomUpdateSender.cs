using KeyWars.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Services;

public interface ILiveRoomUpdateSender
{
    Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class SignalRLiveRoomUpdateSender(IHubContext<ArenaHub> hubContext) : ILiveRoomUpdateSender
{
    public Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken)
    {
        return hubContext.Clients
            .Group(snapshot.RoomId.ToString("N"))
            .SendAsync("roomChanged", snapshot, cancellationToken);
    }
}
