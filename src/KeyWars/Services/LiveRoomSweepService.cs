using KeyWars.Infrastructure.Cluster;

namespace KeyWars.Services;

public sealed class LiveRoomSweepService(
    ILiveRoomDispatcher rooms,
    ILiveRoomUpdateSender updateSender,
    TimeProvider timeProvider,
    ILogger<LiveRoomSweepService> logger,
    RuntimeTopology? topology = null) : BackgroundService
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (topology?.IsCluster == true)
        {
            await base.StopAsync(cancellationToken);
            return;
        }

        var abortedRooms = await rooms.AbortActiveRoomsAsync(CancellationToken.None);
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
        foreach (var snapshot in await rooms.SweepAsync(cancellationToken))
        {
            await updateSender.SendAsync(snapshot, cancellationToken);
        }
    }
}
