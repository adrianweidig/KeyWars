using Microsoft.Extensions.Options;

namespace KeyWars.Data;

public sealed class DataRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    IMaintenanceLease maintenanceLease,
    IOptions<RetentionOptions> configuredOptions,
    TimeProvider timeProvider,
    ILogger<DataRetentionHostedService> logger) : BackgroundService
{
    private readonly RetentionOptions options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        if (!options.Enabled)
        {
            logger.LogInformation("Automatische Retention ist deaktiviert.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var lease = await maintenanceLease.TryAcquireAsync("retention", stoppingToken);
                if (lease is null)
                {
                    logger.LogInformation(
                        "Retention-Zyklus übersprungen: Ein anderer Maintenance-Worker hält den Cluster-Lease.");
                    await DelayUntilNextRunAsync(stoppingToken);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    lease.LeaseLost);
                await scope.ServiceProvider
                    .GetRequiredService<DataRetentionService>()
                    .RunAsync(options.DryRun, operationCancellation.Token);
                lease.ThrowIfLost();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatischer Retention-Lauf ist fehlgeschlagen.");
            }

            await DelayUntilNextRunAsync(stoppingToken);
        }
    }

    private Task DelayUntilNextRunAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromHours(options.IntervalHours), timeProvider, cancellationToken);
}
