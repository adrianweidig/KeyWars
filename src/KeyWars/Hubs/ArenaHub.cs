using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Hubs;

[Authorize]
public sealed class ArenaHub(
    CurrentUser currentUser,
    LiveRoomManager rooms,
    LivePresenceTracker presence,
    LiveProgressBroadcaster progress,
    LiveReactionService reactions) : Hub
{
    public async Task<LiveRoomSnapshot?> JoinRoom(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        presence.EnsureCanConnect(profile.Id, Context.ConnectionId);
        LiveRoomSnapshot snapshot;
        try
        {
            snapshot = rooms.Join(roomId, profile.Id, profile.DisplayName);
        }
        catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
        {
            await NotifyRoomUnavailableAsync(ex.Message);
            return null;
        }

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

    public async Task SendReaction(Guid roomId, string key)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        if (!profile.ReactionsEnabled)
        {
            return;
        }

        var snapshot = rooms.Snapshot(roomId);
        if (!snapshot.Participants.Any(participant => participant.ProfileId == profile.Id))
        {
            throw new InvalidOperationException("Nur aktive Teilnehmende können Arena-Reaktionen senden.");
        }

        var reaction = reactions.TrySubmit(roomId, profile.Id, profile.DisplayName, key);
        if (reaction is null)
        {
            return;
        }

        await Clients.Group(roomId.ToString("N")).SendAsync("reactionReceived", reaction, Context.ConnectionAborted);
    }

    public async Task<LiveRoomSnapshot?> LeaveRoom(Guid roomId)
    {
        try
        {
            var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
            var leave = presence.LeaveRoom(profile.Id, Context.ConnectionId, roomId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
            if (leave is null || !leave.RoomLostLastConnection)
            {
                try
                {
                    return rooms.Snapshot(roomId);
                }
                catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
                {
                    await NotifyRoomUnavailableAsync(ex.Message);
                    return null;
                }
            }

            var snapshot = rooms.Disconnect(leave.RoomId, leave.ProfileId);
            await Clients.Group(leave.RoomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
            return snapshot;
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            return null;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var leave = presence.RemoveConnection(Context.ConnectionId);
        if (leave is not null && leave.RoomLostLastConnection)
        {
            try
            {
                var snapshot = rooms.Disconnect(leave.RoomId, leave.ProfileId);
                await Clients.Group(leave.RoomId.ToString("N")).SendAsync("roomChanged", snapshot);
            }
            catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
            {
            }
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

        try
        {
            var snapshot = rooms.Disconnect(previousRoomId, profileId);
            await Clients.Group(previousRoomId.ToString("N")).SendAsync("roomChanged", snapshot, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
        {
        }
    }

    private Task NotifyRoomUnavailableAsync(string message)
    {
        return Clients.Caller.SendAsync("roomUnavailable", message, Context.ConnectionAborted);
    }

    private static bool IsRoomNotFound(InvalidOperationException exception)
    {
        return exception.Message.Contains("nicht gefunden", StringComparison.OrdinalIgnoreCase);
    }
}
