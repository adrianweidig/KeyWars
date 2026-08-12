namespace KeyWars.Services;

public interface ILiveRoomCompletionDrain
{
    Task<CompletionDrainResult> DrainProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}

public interface ILiveRoomCompletionMonitor
{
    int Capacity { get; }
    int PendingCount { get; }
    int FailedRecordCount { get; }
    long FailedAttempts { get; }
    LiveRoomCompletionMetrics GetMetrics();
}
