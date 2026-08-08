namespace KeyWars.Services;

public sealed class LiveRoomSweepService(
    LiveRoomManager rooms,
    ILiveRoomUpdateSender updateSender,
    TimeProvider timeProvider,
    ILogger<LiveRoomSweepService> logger) : BackgroundService
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var abortedRooms = rooms.AbortActiveRooms();
        if (abortedRooms > 0)
        {
            logger.LogWarning("{Count} laufende Arena-Räume wurden beim Shutdown ohne Rating abgebrochen.", abortedRooms);
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Arena-Raum-Sweep ist fehlgeschlagen.");
            }
        }
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var snapshot in rooms.Sweep())
        {
            await updateSender.SendAsync(snapshot, cancellationToken);
        }
    }
}
