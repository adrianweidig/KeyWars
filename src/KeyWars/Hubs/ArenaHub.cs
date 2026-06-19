using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Hubs;

[Authorize]
public sealed class ArenaHub(CurrentUser currentUser, LiveRoomManager rooms, LivePresenceTracker presence, LiveProgressBroadcaster progress) : Hub
{
    public async Task<LiveRoomSnapshot> JoinRoom(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        presence.EnsureCanConnect(profile.Id, Context.ConnectionId);
        var snapshot = rooms.Join(roomId, profile.Id, profile.DisplayName);
        await ApplyRoomSwitchAsync(profile.Id, presence.EnterRoom(profile.Id, Context.ConnectionId, roomId));
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> JoinRoomByCode(string code)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        presence.EnsureCanConnect(profile.Id, Context.ConnectionId);
        var snapshot = rooms.JoinByCode(code, profile.Id, profile.DisplayName);
        await ApplyRoomSwitchAsync(profile.Id, presence.EnterRoom(profile.Id, Context.ConnectionId, snapshot.RoomId));
        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.RoomId.ToString("N"), Context.ConnectionAborted);
        await Clients.Group(snapshot.RoomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> SetReady(Guid roomId, bool ready)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.SetReady(roomId, profile.Id, ready);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> Start(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.Start(roomId, profile.Id);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task SubmitProgress(Guid roomId, int sequence, string input)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var result = rooms.SubmitProgressDelta(roomId, profile.Id, sequence, input);
        if (result.Snapshot is { } snapshot)
        {
            await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        }

        if (result.Delta is { } delta)
        {
            await progress.PublishAsync(delta, Context.ConnectionAborted);
        }
    }

    public async Task<LiveRoomSnapshot> Finish(Guid roomId, string input, int backspaces, int focusLosses)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.Finish(roomId, profile.Id, input, backspaces, focusLosses);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> GiveUp(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.GiveUp(roomId, profile.Id);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot?> LeaveRoom(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var leave = presence.LeaveRoom(profile.Id, Context.ConnectionId, roomId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
        if (leave is null || !leave.RoomLostLastConnection)
        {
            return rooms.Snapshot(roomId);
        }

        var snapshot = rooms.Disconnect(leave.RoomId, leave.ProfileId);
        await Clients.Group(leave.RoomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var leave = presence.RemoveConnection(Context.ConnectionId);
        if (leave is not null && leave.RoomLostLastConnection)
        {
            var snapshot = rooms.Disconnect(leave.RoomId, leave.ProfileId);
            await Clients.Group(leave.RoomId.ToString("N")).SendAsync("roomChanged", snapshot);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task ApplyRoomSwitchAsync(Guid profileId, LivePresenceSwitch roomSwitch)
    {
        if (roomSwitch.PreviousRoomId is not { } previousRoomId)
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, previousRoomId.ToString("N"), Context.ConnectionAborted);
        if (!roomSwitch.PreviousRoomLostLastConnection)
        {
            return;
        }

        var snapshot = rooms.Disconnect(previousRoomId, profileId);
        await Clients.Group(previousRoomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
    }
}
