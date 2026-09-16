using KeyWars.Services;

namespace KeyWars.Infrastructure.Cluster;

internal sealed class RedisLivePresenceExpiryService(
    ILivePresenceExpiryStore expiryStore,
    ILiveRoomDispatcher rooms,
    IProfileAccessGate accessGate,
    ILiveRoomUpdateSender updates,
    TimeProvider timeProvider,
    ILogger<RedisLivePresenceExpiryService> logger) : BackgroundService
{
    internal const int ClaimsPerBucket = 16;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private int bucketCursor = Random.Shared.Next(RedisClusterKeyspace.BucketCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await SweepNextBucketAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Abgelaufene Arena-Presence konnte nicht verarbeitet werden.");
            }
        }
    }

    internal async Task SweepNextBucketAsync(CancellationToken cancellationToken)
    {
        var bucket = unchecked((byte)(Interlocked.Increment(ref bucketCursor) - 1));
        var batch = await expiryStore.ClaimExpiredAsync(bucket, ClaimsPerBucket, cancellationToken);
        if (batch.InvalidMembers > 0)
        {
            logger.LogWarning(
                "{Count} ungültige Arena-Presence-Ablaufeinträge wurden aus Bucket {Bucket} entfernt.",
                batch.InvalidMembers,
                bucket.ToString("x2"));
        }

        await Task.WhenAll(batch.Claims.Select(claim => ProcessClaimAsync(claim, cancellationToken)));
    }

    internal async Task ProcessClaimAsync(
        ExpiredPresenceClaim claim,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var profileLease = await accessGate.AcquireAsync(claim.ProfileId, cancellationToken);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                profileLease.LeaseLost);
            var operationToken = operationCancellation.Token;
            try
            {
                try
                {
                    LiveRoomSnapshot? snapshot = null;
                    var disconnected = await expiryStore.ExecuteIfRoomEmptyAsync(
                        claim,
                        async lockedToken =>
                        {
                            snapshot = await rooms.DisconnectAsync(claim.RoomId, claim.ProfileId, lockedToken);
                        },
                        operationToken);
                    if (disconnected && snapshot is not null)
                    {
                        await updates.SendAsync(snapshot, operationToken);
                    }
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.Contains("nicht gefunden", StringComparison.OrdinalIgnoreCase))
                {
                }

                await expiryStore.CompleteExpiryClaimAsync(claim, operationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (profileLease.LeaseLost.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Der Presence-Lease für Profil {ProfileId} ging verloren; der Claim bleibt bis zum Visibility-Timeout liegen.",
                    claim.ProfileId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Abgelaufene Arena-Presence für Profil {ProfileId} bleibt bis zum Visibility-Timeout offen.",
                    claim.ProfileId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProfileOperationException exception) when (
            exception.Code is "profile_deleted" or "profile_operation_in_progress")
        {
            await expiryStore.CompleteExpiryClaimAsync(claim, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Der Presence-Claim für Profil {ProfileId} bleibt bis zum Visibility-Timeout liegen.",
                claim.ProfileId);
        }
    }
}
