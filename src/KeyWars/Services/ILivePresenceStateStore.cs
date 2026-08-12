namespace KeyWars.Services;

public interface ILivePresenceStateStore
{
    ValueTask EnsureCanConnectAsync(
        Guid profileId,
        string connectionId,
        CancellationToken cancellationToken = default);
    ValueTask<LivePresenceSwitch> EnterRoomAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        CancellationToken cancellationToken = default);
    ValueTask RollbackEnterRoomAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        LivePresenceSwitch transition,
        CancellationToken cancellationToken = default);
    ValueTask<LivePresenceLeave?> LeaveRoomAsync(
        Guid profileId,
        string connectionId,
        Guid roomId,
        CancellationToken cancellationToken = default);
    ValueTask<LivePresenceLeave?> RemoveConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
    ValueTask<int> CountRoomConnectionsAsync(
        Guid profileId,
        Guid roomId,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> RemoveProfileFromRoomAsync(
        Guid profileId,
        Guid roomId,
        CancellationToken cancellationToken = default);
}
