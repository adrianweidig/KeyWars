using System.Collections.Concurrent;
using System.Threading.Channels;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public interface ILiveRoomCompletionSink
{
    void Enqueue(CompletedRoomRecord record);
}

public interface ILiveRoomCompletionWriter
{
    Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken);
}

public sealed class LiveRoomCompletionQueue(
    IOptions<LiveOptions> options,
    ILiveRoomCompletionWriter completionWriter,
    ILogger<LiveRoomCompletionQueue> logger) : BackgroundService, ILiveRoomCompletionSink
{
    private const int MaxPersistenceAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)];
    private readonly ConcurrentQueue<CompletedRoomRecord> failedRecords = new();
    private readonly SemaphoreSlim processingGate = new(1, 1);
    private readonly Channel<CompletedRoomRecord> records = Channel.CreateBounded<CompletedRoomRecord>(
        new BoundedChannelOptions(Math.Clamp(options.Value.CompletionQueueCapacity, 1, 65_536))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private int queuedRecords;
    private long failedAttempts;

    public int Capacity { get; } = Math.Clamp(options.Value.CompletionQueueCapacity, 1, 65_536);
    public int PendingCount => Volatile.Read(ref queuedRecords) + failedRecords.Count;
    public long FailedAttempts => Volatile.Read(ref failedAttempts);

    public void Enqueue(CompletedRoomRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.IdempotencyKey))
        {
            throw new InvalidOperationException("Arena-Abschlussdaten enthalten keinen Idempotenzschlüssel.");
        }

        if (!records.Writer.TryWrite(record))
        {
            throw new InvalidOperationException("Die Arena-Ergebnisqueue ist voll. Bitte versuche es gleich erneut.");
        }

        Interlocked.Increment(ref queuedRecords);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await processingGate.WaitAsync(cancellationToken);
        try
        {
            while (records.Reader.TryRead(out var record))
            {
                Interlocked.Decrement(ref queuedRecords);
                await PersistOrRetainAsync(record, cancellationToken);
            }

            await RetryFailedRecordsAsync(cancellationToken);
        }
        finally
        {
            processingGate.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        records.Writer.TryComplete();
        if (ExecuteTask is { } executeTask)
        {
            var completedTask = await Task.WhenAny(executeTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (completedTask != executeTask)
            {
                logger.LogWarning("{Count} Arena-Ergebnisjobs sind beim Shutdown noch in Bearbeitung.", PendingCount);
                await base.StopAsync(cancellationToken);
            }
        }

        await FlushAsync(cancellationToken);
        if (PendingCount > 0)
        {
            logger.LogWarning("{Count} Arena-Ergebnisjobs konnten vor dem Shutdown nicht persistiert werden.", PendingCount);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var record in records.Reader.ReadAllAsync(stoppingToken))
            {
                Interlocked.Decrement(ref queuedRecords);
                await processingGate.WaitAsync(stoppingToken);
                try
                {
                    await PersistOrRetainAsync(record, stoppingToken);
                }
                finally
                {
                    processingGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RetryFailedRecordsAsync(CancellationToken cancellationToken)
    {
        var retryBatch = new List<CompletedRoomRecord>();
        while (failedRecords.TryDequeue(out var record))
        {
            retryBatch.Add(record);
        }

        foreach (var record in retryBatch)
        {
            await PersistOrRetainAsync(record, cancellationToken);
        }
    }

    private async Task PersistOrRetainAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        var persisted = await PersistWithRetryAsync(record, cancellationToken);
        if (persisted)
        {
            return;
        }

        failedRecords.Enqueue(record);
        Interlocked.Increment(ref failedAttempts);
    }

    private async Task<bool> PersistWithRetryAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxPersistenceAttempts; attempt++)
        {
            try
            {
                await completionWriter.PersistAsync(record, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientSqliteFailure(ex) && attempt < MaxPersistenceAttempts)
            {
                logger.LogWarning(ex, "Transientes SQLite-Problem bei Arena-Raum {RoomId}; Versuch {Attempt}/{MaxAttempts}.", record.Id, attempt, MaxPersistenceAttempts);
                await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)], cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Arena-Raum {RoomId} konnte nicht persistiert werden; der Job bleibt als fehlgeschlagen sichtbar.", record.Id);
                return false;
            }
        }

        return false;
    }

    private static bool IsTransientSqliteFailure(Exception exception)
    {
        return exception switch
        {
            SqliteException { SqliteErrorCode: 5 or 6 } => true,
            DbUpdateException { InnerException: { } inner } => IsTransientSqliteFailure(inner),
            _ when exception.InnerException is not null => IsTransientSqliteFailure(exception.InnerException),
            _ => false
        };
    }
}

public sealed class SqliteLiveRoomCompletionWriter(IServiceScopeFactory scopeFactory) : ILiveRoomCompletionWriter
{
    public async Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var motivation = scope.ServiceProvider.GetRequiredService<MotivationService>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.LiveRoomSummaries.AnyAsync(item => item.Id == record.Id || item.IdempotencyKey == record.IdempotencyKey, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var participantIds = record.Participants.Select(item => item.UserProfileId).Distinct().ToArray();
        var profiles = await db.UserProfiles.Where(item => participantIds.Contains(item.Id)).ToListAsync(cancellationToken);
        if (profiles.Count != participantIds.Length)
        {
            throw new InvalidOperationException("Mindestens ein Arena-Teilnehmerprofil fehlt für die Ergebnispersistenz.");
        }

        var ratingChanges = profiles.ToDictionary(profile => profile.Id, profile => new RatingChange(profile.Id, profile.ArenaRating, 0, profile.ArenaRating));
        var rankingInput = record.Participants
            .Select(item => new RaceResult(
                item.UserProfileId,
                item.Status,
                item.DurationMilliseconds,
                item.Accuracy,
                0,
                100,
                item.Wpm,
                0))
            .ToArray();
        var ranked = RaceRanking.RankClassic(rankingInput);
        var isServerAbort = record.Participants.Any(item => item.Status == ParticipantStatus.AbortedByServer);
        if (!isServerAbort && ranked.Count >= 2)
        {
            var ratings = profiles.ToDictionary(item => item.Id, item => item.ArenaRating);
            ratingChanges = MultiplayerRating.CalculatePairwiseEloChanges(ratings, ranked).ToDictionary(item => item.Key, item => item.Value);
            foreach (var profile in profiles)
            {
                var participant = record.Participants.Single(item => item.UserProfileId == profile.Id);
                var ratingChange = ratingChanges[profile.Id];
                profile.ArenaRating = ratingChange.RatingAfter;
                profile.RatedMatchCount++;
                profile.SeasonPoints += Math.Max(1, (int)Math.Round(participant.Wpm / 10d));
                profile.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        db.LiveRoomSummaries.Add(new LiveRoomSummary
        {
            Id = record.Id,
            RoundNumber = record.RoundNumber,
            RoundVersion = record.RoundVersion,
            IdempotencyKey = record.IdempotencyKey,
            CreatorProfileId = record.CreatorProfileId,
            RoomCode = record.RoomCode,
            Mode = record.Mode,
            Visibility = record.Visibility,
            RoundCount = record.RoundCount,
            CreatedAt = record.CreatedAt,
            StartedAt = record.StartedAt,
            FinishedAt = record.FinishedAt,
            AbortedByServer = isServerAbort
        });

        foreach (var participant in record.Participants)
        {
            var ratingChange = ratingChanges[participant.UserProfileId];
            db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
            {
                LiveRoomSummaryId = record.Id,
                UserProfileId = participant.UserProfileId,
                Status = participant.Status,
                Placement = participant.Placement,
                DurationMilliseconds = participant.DurationMilliseconds,
                Wpm = participant.Wpm,
                Accuracy = participant.Accuracy,
                RatingBefore = ratingChange.RatingBefore,
                RatingDelta = ratingChange.RatingDelta,
                RatingAfter = ratingChange.RatingAfter
            });

            if (!isServerAbort && participant.Status == ParticipantStatus.Finished)
            {
                await motivation.ApplyArenaResultAsync(
                    participant.UserProfileId,
                    $"{record.IdempotencyKey}:{participant.UserProfileId:N}",
                    participant.Wpm,
                    participant.Accuracy,
                    participant.DurationMilliseconds,
                    cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
