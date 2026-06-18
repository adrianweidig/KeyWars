using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Hubs;

[Authorize]
public sealed class ArenaHub(CurrentUser currentUser, LiveRoomManager rooms, TypingEngine typingEngine) : Hub
{
    public async Task<LiveRoomSnapshot> JoinRoom(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.Join(roomId, profile.Id, profile.DisplayName);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> JoinRoomByCode(string code)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = rooms.JoinByCode(code, profile.Id, profile.DisplayName);
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

    public async Task SubmitProgress(Guid roomId, int sequence, int correctCharacters, double wpm)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        if (rooms.SubmitProgress(roomId, profile.Id, sequence, correctCharacters, wpm))
        {
            var snapshot = rooms.Snapshot(roomId);
            await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        }
    }

    public async Task<LiveRoomSnapshot> Finish(Guid roomId, string targetText, string input, int backspaces, int focusLosses, int durationMilliseconds)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var metrics = typingEngine.Analyze(targetText, input, TimeSpan.FromMilliseconds(Math.Max(1, durationMilliseconds)), backspaces, focusLosses);
        var snapshot = rooms.Finish(roomId, profile.Id, metrics);
        await Clients.Group(roomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        return snapshot;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
