using KeyWars.Data;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed class SeasonRolloverHostedService(
    IServiceScopeFactory scopeFactory,
    IMaintenanceLease maintenanceLease,
    IOptions<SeasonOptions> configuredOptions,
    TimeProvider timeProvider,
    ILogger<SeasonRolloverHostedService> logger) : BackgroundService
{
    private readonly SeasonOptions options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        if (!options.Enabled)
        {
            logger.LogInformation("Saisonwertung ist deaktiviert.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var lease = await maintenanceLease.TryAcquireAsync("season-rollover", stoppingToken);
                if (lease is not null)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken,
                        lease.LeaseLost);
                    var season = await scope.ServiceProvider
                        .GetRequiredService<SeasonService>()
                        .EnsureCurrentAsync(operationCancellation.Token);
                    lease.ThrowIfLost();
                    logger.LogDebug("Saison {SeasonKey} ist aktiv.", season.Key);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Saison-Rollover ist fehlgeschlagen.");
            }

            await Task.Delay(
                TimeSpan.FromMinutes(options.RolloverCheckMinutes),
                timeProvider,
                stoppingToken);
        }
    }
}
