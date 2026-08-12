namespace KeyWars.Services;

public interface ILiveRoomDispatcher
{
    ValueTask<LiveRoomSnapshot> CreateRoomAsync(
        CreateLiveRoomRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<LiveRoomSnapshot>> ListOpenRoomsAsync(
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomLobbyPage> ListLobbySummariesAsync(
        Guid viewerProfileId,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomMetricsSnapshot> MetricsSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<Guid> ResolveRoomIdByCodeAsync(string code, CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> JoinByCodeAsync(
        string code,
        Guid profileId,
        string displayName,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> JoinAsync(
        Guid roomId,
        Guid profileId,
        string displayName,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> SetReadyAsync(
        Guid roomId,
        Guid profileId,
        bool ready,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> SetLobbyLockedAsync(
        Guid roomId,
        Guid hostProfileId,
        bool locked,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> TransferHostAsync(
        Guid roomId,
        Guid hostProfileId,
        Guid nextHostProfileId,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> KickAsync(
        Guid roomId,
        Guid hostProfileId,
        Guid targetProfileId,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> CloseAsync(
        Guid roomId,
        Guid hostProfileId,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> StartAsync(
        Guid roomId,
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> SubmitProgressAsync(
        Guid roomId,
        Guid profileId,
        int sequence,
        string input,
        CancellationToken cancellationToken = default);
    ValueTask<LiveProgressResult> SubmitProgressDeltaAsync(
        Guid roomId,
        Guid profileId,
        int sequence,
        string input,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> FinishAsync(
        Guid roomId,
        Guid profileId,
        string input,
        int backspaces,
        int focusLosses,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> GiveUpAsync(
        Guid roomId,
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> DisconnectAsync(
        Guid roomId,
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask<LiveRoomSnapshot> SnapshotAsync(Guid roomId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<LiveRoomSnapshot>> SweepAsync(CancellationToken cancellationToken = default);
    ValueTask RemoveProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    ValueTask<int> AbortActiveRoomsAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalLiveRoomDispatcher(LiveRoomManager rooms) : ILiveRoomDispatcher
{
    public ValueTask<LiveRoomSnapshot> CreateRoomAsync(CreateLiveRoomRequest request, CancellationToken cancellationToken = default) =>
        Result(rooms.CreateRoom(request), cancellationToken);

    public ValueTask<IReadOnlyList<LiveRoomSnapshot>> ListOpenRoomsAsync(CancellationToken cancellationToken = default) =>
        Result(rooms.ListOpenRooms(), cancellationToken);

    public ValueTask<LiveRoomLobbyPage> ListLobbySummariesAsync(Guid viewerProfileId, int offset = 0, int limit = 20, CancellationToken cancellationToken = default) =>
        Result(rooms.ListLobbySummaries(viewerProfileId, offset, limit), cancellationToken);

    public ValueTask<LiveRoomMetricsSnapshot> MetricsSnapshotAsync(CancellationToken cancellationToken = default) =>
        Result(rooms.MetricsSnapshot(), cancellationToken);

    public ValueTask<Guid> ResolveRoomIdByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Result(rooms.ResolveRoomIdByCode(code), cancellationToken);

    public ValueTask<LiveRoomSnapshot> JoinByCodeAsync(string code, Guid profileId, string displayName, CancellationToken cancellationToken = default) =>
        Result(rooms.JoinByCode(code, profileId, displayName), cancellationToken);

    public ValueTask<LiveRoomSnapshot> JoinAsync(Guid roomId, Guid profileId, string displayName, CancellationToken cancellationToken = default) =>
        Result(rooms.Join(roomId, profileId, displayName), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SetReadyAsync(Guid roomId, Guid profileId, bool ready, CancellationToken cancellationToken = default) =>
        Result(rooms.SetReady(roomId, profileId, ready), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SetLobbyLockedAsync(Guid roomId, Guid hostProfileId, bool locked, CancellationToken cancellationToken = default) =>
        Result(rooms.SetLobbyLocked(roomId, hostProfileId, locked), cancellationToken);

    public ValueTask<LiveRoomSnapshot> TransferHostAsync(Guid roomId, Guid hostProfileId, Guid nextHostProfileId, CancellationToken cancellationToken = default) =>
        Result(rooms.TransferHost(roomId, hostProfileId, nextHostProfileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> KickAsync(Guid roomId, Guid hostProfileId, Guid targetProfileId, CancellationToken cancellationToken = default) =>
        Result(rooms.Kick(roomId, hostProfileId, targetProfileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> CloseAsync(Guid roomId, Guid hostProfileId, CancellationToken cancellationToken = default) =>
        Result(rooms.Close(roomId, hostProfileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> StartAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) =>
        Result(rooms.Start(roomId, profileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SubmitProgressAsync(Guid roomId, Guid profileId, int sequence, string input, CancellationToken cancellationToken = default) =>
        Result(rooms.SubmitProgress(roomId, profileId, sequence, input), cancellationToken);

    public ValueTask<LiveProgressResult> SubmitProgressDeltaAsync(Guid roomId, Guid profileId, int sequence, string input, CancellationToken cancellationToken = default) =>
        Result(rooms.SubmitProgressDelta(roomId, profileId, sequence, input), cancellationToken);

    public ValueTask<LiveRoomSnapshot> FinishAsync(Guid roomId, Guid profileId, string input, int backspaces, int focusLosses, CancellationToken cancellationToken = default) =>
        Result(rooms.Finish(roomId, profileId, input, backspaces, focusLosses), cancellationToken);

    public ValueTask<LiveRoomSnapshot> GiveUpAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) =>
        Result(rooms.GiveUp(roomId, profileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> DisconnectAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) =>
        Result(rooms.Disconnect(roomId, profileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SnapshotAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        Result(rooms.Snapshot(roomId), cancellationToken);

    public ValueTask<IReadOnlyList<LiveRoomSnapshot>> SweepAsync(CancellationToken cancellationToken = default) =>
        Result(rooms.Sweep(), cancellationToken);

    public ValueTask RemoveProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        rooms.RemoveProfile(profileId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> AbortActiveRoomsAsync(CancellationToken cancellationToken = default) =>
        Result(rooms.AbortActiveRooms(), cancellationToken);

    private static ValueTask<T> Result<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }
}
