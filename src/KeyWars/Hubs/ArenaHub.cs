using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Hubs;

[Authorize]
public sealed class ArenaHub(
    CurrentUser currentUser,
    ILiveRoomDispatcher rooms,
    ILivePresenceStateStore presence,
    LiveProgressBroadcaster progress,
    LiveReactionService reactions,
    IProfileAccessGate accessGate,
    ISharedRateLimiter rateLimiter,
    ILiveRoomUpdateSender updates) : Hub
{
    public async Task<LiveRoomSnapshot?> JoinRoom(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var roomSwitch = await presence.EnterRoomAsync(
            profile.Id,
            Context.ConnectionId,
            roomId,
            Context.ConnectionAborted);
        LiveRoomSnapshot snapshot;
        try
        {
            snapshot = await rooms.JoinAsync(roomId, profile.Id, profile.DisplayName, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
        {
            await presence.RollbackEnterRoomAsync(
                profile.Id,
                Context.ConnectionId,
                roomId,
                roomSwitch,
                CancellationToken.None);
            await NotifyRoomUnavailableAsync(ex.Message);
            return null;
        }
        catch
        {
            await presence.RollbackEnterRoomAsync(
                profile.Id,
                Context.ConnectionId,
                roomId,
                roomSwitch,
                CancellationToken.None);
            throw;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
        await ApplyRoomSwitchAsync(profile.Id, roomSwitch);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> JoinRoomByCode(string code)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var roomId = await rooms.ResolveRoomIdByCodeAsync(code, Context.ConnectionAborted);
        var roomSwitch = await presence.EnterRoomAsync(
            profile.Id,
            Context.ConnectionId,
            roomId,
            Context.ConnectionAborted);
        LiveRoomSnapshot snapshot;
        try
        {
            snapshot = await rooms.JoinByCodeAsync(code, profile.Id, profile.DisplayName, Context.ConnectionAborted);
        }
        catch
        {
            await presence.RollbackEnterRoomAsync(
                profile.Id,
                Context.ConnectionId,
                roomId,
                roomSwitch,
                CancellationToken.None);
            throw;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.RoomId.ToString("N"), Context.ConnectionAborted);
        await ApplyRoomSwitchAsync(profile.Id, roomSwitch);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> SetReady(Guid roomId, bool ready)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.SetReadyAsync(roomId, profile.Id, ready, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> Start(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.StartAsync(roomId, profile.Id, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task SubmitProgress(Guid roomId, int sequence, string input)
    {
        var profileId = currentUser.GetProfileId(Context.User!)
            ?? throw new InvalidOperationException("Die aktuelle Sitzung besitzt kein gültiges KeyWars-Profil.");
        var result = await rooms.SubmitProgressDeltaAsync(roomId, profileId, sequence, input, Context.ConnectionAborted);
        if (result.Snapshot is { } snapshot)
        {
            await updates.SendAsync(snapshot, CancellationToken.None);
        }

        if (result.Delta is { } delta)
        {
            await progress.PublishAsync(delta, Context.ConnectionAborted);
        }
    }

    public async Task<LiveRoomSnapshot> Finish(Guid roomId, string input, int backspaces, int focusLosses)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.FinishAsync(
            roomId,
            profile.Id,
            input,
            backspaces,
            focusLosses,
            Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> GiveUp(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.GiveUpAsync(roomId, profile.Id, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> SetLobbyLocked(Guid roomId, bool locked)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.SetLobbyLockedAsync(roomId, profile.Id, locked, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> TransferHost(Guid roomId, Guid nextHostProfileId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.TransferHostAsync(
            roomId,
            profile.Id,
            nextHostProfileId,
            Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> Kick(Guid roomId, Guid targetProfileId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.KickAsync(roomId, profile.Id, targetProfileId, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        var removedConnections = await presence.RemoveProfileFromRoomAsync(
            targetProfileId,
            roomId,
            CancellationToken.None);
        foreach (var connectionId in removedConnections)
        {
            await Clients.Client(connectionId).SendAsync(
                "roomUnavailable",
                "Du wurdest durch die Raumleitung aus diesem Raum entfernt.",
                CancellationToken.None);
            await Groups.RemoveFromGroupAsync(connectionId, roomId.ToString("N"), CancellationToken.None);
        }

        return snapshot;
    }

    public async Task<LiveRoomSnapshot> Close(Guid roomId)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        var snapshot = await rooms.CloseAsync(roomId, profile.Id, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        await Clients.Group(roomId.ToString("N")).SendAsync(
            "roomUnavailable",
            snapshot.CloseReason ?? "Der Raum wurde geschlossen.",
            CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomLobbyPage> GetLobbyPage(int offset = 0, int limit = 20)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        return await rooms.ListLobbySummariesAsync(profile.Id, offset, limit, Context.ConnectionAborted);
    }

    public async Task SendReaction(Guid roomId, string key)
    {
        var profile = await currentUser.RequireProfileAsync(Context.User!, Context.ConnectionAborted);
        if (!profile.ReactionsEnabled)
        {
            return;
        }

        if (!await rateLimiter.TryAcquireAsync(
                "reaction",
                profile.Id.ToString("N"),
                12,
                TimeSpan.FromMinutes(1),
                Context.ConnectionAborted))
        {
            return;
        }

        var snapshot = await rooms.SnapshotAsync(roomId, Context.ConnectionAborted);
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
            var leave = await presence.LeaveRoomAsync(profile.Id, Context.ConnectionId, roomId, Context.ConnectionAborted);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
            if (leave is null || !leave.RoomLostLastConnection)
            {
                try
                {
                    return await rooms.SnapshotAsync(roomId, Context.ConnectionAborted);
                }
                catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
                {
                    await NotifyRoomUnavailableAsync(ex.Message);
                    return null;
                }
            }

            var snapshot = await rooms.DisconnectAsync(
                leave.RoomId,
                leave.ProfileId,
                Context.ConnectionAborted);
            await updates.SendAsync(snapshot, CancellationToken.None);
            return snapshot;
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            return null;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        IOperationLease? accessLease = null;
        var profileIdValue = Context.User?.FindFirst(KeyWarsClaims.ProfileId)?.Value;
        if (Guid.TryParse(profileIdValue, out var profileId))
        {
            try
            {
                accessLease = await accessGate.AcquireAsync(profileId, CancellationToken.None);
            }
            catch (ProfileOperationException)
            {
                await presence.RemoveConnectionAsync(Context.ConnectionId, CancellationToken.None);
                await base.OnDisconnectedAsync(exception);
                return;
            }
        }

        await using (accessLease)
        {
            var operationToken = accessLease?.LeaseLost ?? CancellationToken.None;
            var leave = await presence.RemoveConnectionAsync(Context.ConnectionId, operationToken);
            if (leave is not null && leave.RoomLostLastConnection)
            {
                try
                {
                    var snapshot = await rooms.DisconnectAsync(
                        leave.RoomId,
                        leave.ProfileId,
                        operationToken);
                    await updates.SendAsync(snapshot, operationToken);
                }
                catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
                {
                }
            }

            await base.OnDisconnectedAsync(exception);
        }
    }

    private async Task ApplyRoomSwitchAsync(Guid profileId, LivePresenceSwitch roomSwitch)
    {
        if (!roomSwitch.Changed || roomSwitch.PreviousRoomId is not { } previousRoomId)
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
            var snapshot = await rooms.DisconnectAsync(
                previousRoomId,
                profileId,
                CancellationToken.None);
            await updates.SendAsync(snapshot, CancellationToken.None);
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
