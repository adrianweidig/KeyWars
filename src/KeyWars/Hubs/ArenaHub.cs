using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace KeyWars.Hubs;

[Authorize]
public sealed class ArenaHub(CurrentUser currentUser, LiveRoomManager rooms) : Hub
{
    private static readonly ConcurrentDictionary<string, (Guid RoomId, Guid ProfileId)> Connections = new();

    public async Task<LiveRoomSnapshot> JoinRoom(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.Join(roomId, profile.Id, profile.DisplayName);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
        Connections[Context.ConnectionId] = (roomId, profile.Id);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> JoinRoomByCode(string code)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.JoinByCode(code, profile.Id, profile.DisplayName);
        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.RoomId.ToString("N"), Context.ConnectionAborted);
        Connections[Context.ConnectionId] = (snapshot.RoomId, profile.Id);
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
        var snapshot = rooms.SubmitProgress(roomId, profile.Id, sequence, input);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
    }

    public async Task<LiveRoomSnapshot> Finish(Guid roomId, string input, int backspaces, int focusLosses)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.Finish(roomId, profile.Id, input, backspaces, focusLosses);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Connections.TryRemove(Context.ConnectionId, out var connection))
        {
            var snapshot = rooms.Disconnect(connection.RoomId, connection.ProfileId);
            await Clients.Group(connection.RoomId.ToString("N")).SendAsync("roomChanged", snapshot);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
