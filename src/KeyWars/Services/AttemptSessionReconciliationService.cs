using KeyWars.Data;

namespace KeyWars.Services;

public sealed class AttemptSessionReconciliationService(
    IServiceScopeFactory scopeFactory,
    IMaintenanceLease maintenanceLease,
    TimeProvider timeProvider,
    ILogger<AttemptSessionReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxBatchesPerRun = 10;
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Abgleich verwaister Versuchssitzungen ist fehlgeschlagen.");
            }

            await Task.Delay(Interval, timeProvider, stoppingToken);
        }
    }

    internal async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        await using var lease = await maintenanceLease.TryAcquireAsync(
            "attempt-session-reconciliation",
            cancellationToken);
        if (lease is null)
        {
            return;
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.LeaseLost);
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processed = await scope.ServiceProvider
                .GetRequiredService<AttemptService>()
                .ReconcileExpiredDatabaseAttemptsAsync(operationCancellation.Token);
            lease.ThrowIfLost();
            if (processed < BatchSize)
            {
                break;
            }
        }
    }
}
