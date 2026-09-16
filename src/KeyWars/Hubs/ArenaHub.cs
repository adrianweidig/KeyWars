using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Infrastructure;
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
    ILiveRoomUpdateSender updates,
    ProfileHubConnectionRegistry hubConnections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var profileId = RequireProfileId();
        await presence.EnsureCanConnectAsync(profileId, Context.ConnectionId, Context.ConnectionAborted);
        await hubConnections.RegisterAsync(profileId, Context, Context.ConnectionAborted);
        try
        {
            await base.OnConnectedAsync();
        }
        catch
        {
            await hubConnections.UnregisterAsync(Context.ConnectionId, CancellationToken.None);
            throw;
        }
    }

    public async Task<LiveRoomSnapshot?> JoinRoom(Guid roomId)
    {
        var (profileId, displayName) = RequireProfileIdentity();
        var roomSwitch = await presence.EnterRoomAsync(
            profileId,
            Context.ConnectionId,
            roomId,
            Context.ConnectionAborted);
        LiveRoomSnapshot snapshot;
        try
        {
            snapshot = await rooms.JoinAsync(roomId, profileId, displayName, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex) when (IsRoomNotFound(ex))
        {
            await presence.RollbackEnterRoomAsync(
                profileId,
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
                profileId,
                Context.ConnectionId,
                roomId,
                roomSwitch,
                CancellationToken.None);
            throw;
        }

        await AddToTrackedRoomAsync(roomId);
        await ApplyRoomSwitchAsync(profileId, roomSwitch);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> JoinRoomByCode(string code)
    {
        var (profileId, displayName) = RequireProfileIdentity();
        var roomId = await rooms.ResolveRoomIdByCodeAsync(code, Context.ConnectionAborted);
        var roomSwitch = await presence.EnterRoomAsync(
            profileId,
            Context.ConnectionId,
            roomId,
            Context.ConnectionAborted);
        LiveRoomSnapshot snapshot;
        try
        {
            snapshot = await rooms.JoinByCodeAsync(code, profileId, displayName, Context.ConnectionAborted);
        }
        catch
        {
            await presence.RollbackEnterRoomAsync(
                profileId,
                Context.ConnectionId,
                roomId,
                roomSwitch,
                CancellationToken.None);
            throw;
        }

        await AddToTrackedRoomAsync(snapshot.RoomId);
        await ApplyRoomSwitchAsync(profileId, roomSwitch);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> SetReady(Guid roomId, bool ready)
    {
        var snapshot = await rooms.SetReadyAsync(roomId, RequireProfileId(), ready, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> Start(Guid roomId)
    {
        var snapshot = await rooms.StartAsync(roomId, RequireProfileId(), Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveProgressInputAck> SubmitProgress(Guid roomId, LiveProgressInputDelta input)
    {
        var profileId = RequireProfileId();
        var result = await rooms.SubmitProgressDeltaAsync(
            roomId,
            profileId,
            input.Revision,
            LiveRoomProgress.SerializeInputDelta(input),
            Context.ConnectionAborted);
        if (result.Snapshot is { } snapshot)
        {
            await updates.SendAsync(snapshot, CancellationToken.None);
        }

        if (!result.RelayedByDispatcher &&
            result.Delta is { InputStatus: LiveProgressInputStatus.Applied } delta)
        {
            await progress.PublishAsync(delta, Context.ConnectionAborted);
        }

        var status = result.Delta?.InputStatus ?? LiveProgressInputStatus.Duplicate;
        return new LiveProgressInputAck(
            result.Delta?.ParticipantSequence ?? input.BaseRevision,
            status == LiveProgressInputStatus.Applied,
            status != LiveProgressInputStatus.Applied);
    }

    public async Task<LiveRoomSnapshot> Finish(Guid roomId, string input, int backspaces, int focusLosses)
    {
        var profileId = RequireProfileId();
        var snapshot = await rooms.FinishAsync(
            roomId,
            profileId,
            input,
            backspaces,
            focusLosses,
            Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> GiveUp(Guid roomId)
    {
        var snapshot = await rooms.GiveUpAsync(roomId, RequireProfileId(), Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<bool> Heartbeat(Guid roomId)
    {
        return await presence.RefreshConnectionAsync(
            RequireProfileId(),
            Context.ConnectionId,
            roomId,
            Context.ConnectionAborted);
    }

    public async Task<LiveRoomSnapshot> SetLobbyLocked(Guid roomId, bool locked)
    {
        var snapshot = await rooms.SetLobbyLockedAsync(roomId, RequireProfileId(), locked, Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> TransferHost(Guid roomId, Guid nextHostProfileId)
    {
        var profileId = RequireProfileId();
        var snapshot = await rooms.TransferHostAsync(
            roomId,
            profileId,
            nextHostProfileId,
            Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomSnapshot> Kick(Guid roomId, Guid targetProfileId)
    {
        var snapshot = await rooms.KickAsync(roomId, RequireProfileId(), targetProfileId, Context.ConnectionAborted);
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
        var snapshot = await rooms.CloseAsync(roomId, RequireProfileId(), Context.ConnectionAborted);
        await updates.SendAsync(snapshot, CancellationToken.None);
        await Clients.Group(roomId.ToString("N")).SendAsync(
            "roomUnavailable",
            snapshot.CloseReason ?? "Der Raum wurde geschlossen.",
            CancellationToken.None);
        return snapshot;
    }

    public async Task<LiveRoomLobbyPage> GetLobbyPage(int offset = 0, int limit = 20)
    {
        return await rooms.ListLobbySummariesAsync(RequireProfileId(), offset, limit, Context.ConnectionAborted);
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

        var reaction = reactions.TrySubmit(roomId, profile.Id, profile.DisplayName, key);
        if (reaction is null)
        {
            return;
        }

        var snapshot = await rooms.SnapshotForViewerAsync(roomId, profile.Id, Context.ConnectionAborted);
        if (!snapshot.Participants.Any(participant => participant.ProfileId == profile.Id))
        {
            throw new InvalidOperationException("Nur aktive Teilnehmende können Arena-Reaktionen senden.");
        }

        await Clients.Group(roomId.ToString("N")).SendAsync("reactionReceived", reaction, Context.ConnectionAborted);
    }

    public async Task<LiveRoomSnapshot?> LeaveRoom(Guid roomId)
    {
        try
        {
            var profileId = RequireProfileId();
            var leave = await presence.LeaveRoomAsync(profileId, Context.ConnectionId, roomId, Context.ConnectionAborted);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
            await hubConnections.SetRoomAsync(Context.ConnectionId, null, Context.ConnectionAborted);
            if (leave is null || !leave.RoomLostLastConnection)
            {
                try
                {
                    return await rooms.SnapshotForViewerAsync(roomId, profileId, Context.ConnectionAborted);
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
        try
        {
            IOperationLease? accessLease = null;
            var profileIdValue = Context.User?.FindFirst(KeyWarsClaims.ProfileId)?.Value;
            if (!Guid.TryParse(profileIdValue, out var profileId))
            {
                await base.OnDisconnectedAsync(exception);
                return;
            }

            try
            {
                accessLease = await accessGate.AcquireAsync(profileId, CancellationToken.None);
            }
            catch (ProfileOperationException)
            {
                await presence.RemoveConnectionAsync(profileId, Context.ConnectionId, CancellationToken.None);
                await base.OnDisconnectedAsync(exception);
                return;
            }

            await using (accessLease)
            {
                var operationToken = accessLease?.LeaseLost ?? CancellationToken.None;
                var leave = await presence.RemoveConnectionAsync(profileId, Context.ConnectionId, operationToken);
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
        finally
        {
            await hubConnections.UnregisterAsync(Context.ConnectionId, CancellationToken.None);
        }
    }

    private async Task AddToTrackedRoomAsync(Guid roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString("N"), Context.ConnectionAborted);
        try
        {
            await hubConnections.SetRoomAsync(Context.ConnectionId, roomId, Context.ConnectionAborted);
        }
        catch
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString("N"), CancellationToken.None);
            Context.Abort();
            throw;
        }
    }

    private async Task ApplyRoomSwitchAsync(Guid profileId, LivePresenceSwitch roomSwitch)
    {
        if (!roomSwitch.Changed || roomSwitch.PreviousRoomId is not { } previousRoomId)
        {
            return;
        }

        try
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                previousRoomId.ToString("N"),
                Context.ConnectionAborted);
        }
        catch
        {
            Context.Abort();
            throw;
        }
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

    private Guid RequireProfileId() =>
        currentUser.GetProfileId(Context.User!)
        ?? throw new InvalidOperationException("Die aktuelle Sitzung besitzt kein gültiges KeyWars-Profil.");

    private (Guid ProfileId, string DisplayName) RequireProfileIdentity()
    {
        var profileId = RequireProfileId();
        var displayName = Context.User?.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Die aktuelle Sitzung besitzt keinen gültigen Anzeigenamen.");
        }

        return (profileId, displayName);
    }

    private static bool IsRoomNotFound(InvalidOperationException exception)
    {
        return exception.Message.Contains("nicht gefunden", StringComparison.OrdinalIgnoreCase);
    }
}
