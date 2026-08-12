using KeyWars.Services;

namespace KeyWars.Infrastructure.Observability;

public sealed class OperationalMetricsService(
    ILiveRoomDispatcher rooms,
    ILiveRoomCompletionMonitor completion,
    KeyWarsTelemetry telemetry,
    ILogger<OperationalMetricsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await UpdateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await UpdateAsync(stoppingToken);
        }
    }

    private async Task UpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await rooms.MetricsSnapshotAsync(cancellationToken);
            telemetry.SetArenaSnapshot(snapshot.ActiveRooms, snapshot.Participants);
            telemetry.SetCompletionQueueSnapshot(
                completion.PendingCount + completion.FailedRecordCount,
                completion.OldestPendingAge);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Die operativen Arena-Metriken konnten nicht aktualisiert werden.");
        }
    }
}
