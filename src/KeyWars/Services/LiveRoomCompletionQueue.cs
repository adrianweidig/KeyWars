using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public enum CompletionState
{
    Pending,
    Persisted,
    Failed,
    AbortedUnconfirmed
}

public sealed record CompletionReceipt(Guid RoomId, string IdempotencyKey, CompletionState State);

public sealed record CompletionStatusSnapshot(CompletionState State);

public enum CompletionDrainStatus
{
    Success,
    Timeout,
    Failed
}

public sealed record CompletionDrainResult(CompletionDrainStatus Status, int PendingJobs, int FailedJobs);

public sealed record LiveRoomCompletionMetrics(
    int PendingJobs,
    int FailedRecords,
    long RetryAttempts,
    long PersistedCompletions,
    long FailedCompletions,
    long AbortedUnconfirmedCompletions,
    double AveragePersistenceDurationMilliseconds);

public interface ILiveRoomCompletionSink
{
    CompletionReceipt Enqueue(CompletedRoomRecord record);

    CompletionStatusSnapshot GetStatus(Guid roomId);

    bool CanAcceptNewRoom(int currentRoomCount);
}

public interface ILiveRoomCompletionWriter
{
    Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken);
}

public sealed class LiveRoomCompletionQueue : BackgroundService,
    ILiveRoomCompletionSink,
    ILiveRoomCompletionDrain,
    ILiveRoomCompletionMonitor
{
    private readonly ILiveRoomCompletionWriter completionWriter;
    private readonly LiveRoomCompletionOutbox outbox;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LiveRoomCompletionQueue> logger;
    private readonly object enqueueGate = new();
    private readonly SemaphoreSlim processingGate = new(1, 1);
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private readonly TimeSpan defaultDrainTimeout;
    private int acceptingRecords = 1;
    private long retryAttempts;
    private long persistedCompletions;
    private long failedCompletions;
    private long measuredPersistenceOperations;
    private long totalPersistenceStopwatchTicks;

    public LiveRoomCompletionQueue(
        IOptions<LiveOptions> options,
        ILiveRoomCompletionWriter completionWriter,
        LiveRoomCompletionOutbox outbox,
        TimeProvider timeProvider,
        ILogger<LiveRoomCompletionQueue> logger)
    {
        var liveOptions = options.Value;
        ValidateOptions(liveOptions);
        this.completionWriter = completionWriter;
        this.outbox = outbox;
        this.timeProvider = timeProvider;
        this.logger = logger;
        Capacity = liveOptions.CompletionQueueCapacity;
        FailedRecordLimit = Capacity;
        defaultDrainTimeout = TimeSpan.FromSeconds(liveOptions.CompletionDrainTimeoutSeconds);
    }

    public int Capacity { get; }
    public int FailedRecordLimit { get; }
    public int PendingCount => outbox.GetSnapshot(timeProvider.GetUtcNow()).PendingCount;
    public int FailedRecordCount => outbox.GetSnapshot(timeProvider.GetUtcNow()).FailedCount;
    public long FailedAttempts => Volatile.Read(ref failedCompletions);
    public long RetryAttempts => Volatile.Read(ref retryAttempts);
    public TimeSpan OldestPendingAge => outbox.GetSnapshot(timeProvider.GetUtcNow()).OldestPendingAge;

    public CompletionReceipt Enqueue(CompletedRoomRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.IdempotencyKey))
        {
            throw new InvalidOperationException("Arena-Abschlussdaten enthalten keinen Idempotenzschlüssel.");
        }

        lock (enqueueGate)
        {
            var admission = outbox.Admit(
                record,
                Capacity,
                Volatile.Read(ref acceptingRecords) != 0,
                timeProvider.GetUtcNow());
            if (admission.State == CompletionState.Pending)
            {
                SignalWorker();
            }
            else if (admission.Created)
            {
                Interlocked.Increment(ref failedCompletions);
                logger.LogError(
                    "Ein Arena-Ergebnis wurde dauerhaft gesichert, kann wegen erschöpfter Persistenzkapazität aber erst nach Redrive verarbeitet werden.");
            }

            return new CompletionReceipt(record.Id, record.IdempotencyKey, admission.State);
        }
    }

    public CompletionStatusSnapshot GetStatus(Guid roomId)
    {
        return new CompletionStatusSnapshot(outbox.GetStatus(roomId).State);
    }

    public bool CanAcceptNewRoom(int currentRoomCount)
    {
        if (currentRoomCount < 0 || Volatile.Read(ref acceptingRecords) == 0)
        {
            return false;
        }

        try
        {
            var snapshot = outbox.GetSnapshot(timeProvider.GetUtcNow());
            return snapshot.FailedCount < FailedRecordLimit &&
                currentRoomCount + snapshot.PendingCount + snapshot.FailedCount < Capacity;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Die lokale Completion-Outbox ist nicht erreichbar; neue Arenen werden fail-closed abgelehnt.");
            return false;
        }
    }

    public LiveRoomCompletionMetrics GetMetrics()
    {
        var operations = Volatile.Read(ref measuredPersistenceOperations);
        var stopwatchTicks = Volatile.Read(ref totalPersistenceStopwatchTicks);
        var averageMilliseconds = operations == 0
            ? 0
            : Math.Round(stopwatchTicks * 1000d / Stopwatch.Frequency / operations, 2);
        var snapshot = outbox.GetSnapshot(timeProvider.GetUtcNow());
        return new LiveRoomCompletionMetrics(
            snapshot.PendingCount,
            snapshot.FailedCount,
            RetryAttempts,
            Volatile.Read(ref persistedCompletions),
            Volatile.Read(ref failedCompletions),
            0,
            averageMilliseconds);
    }

    public Task<CompletionDrainResult> DrainProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        return DrainProfileAsync(profileId, defaultDrainTimeout, cancellationToken);
    }

    public async Task<CompletionDrainResult> DrainProfileAsync(Guid profileId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var related = outbox.GetProfileSnapshot(profileId);
            if (related.FailedCount > 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Failed, 0, related.FailedCount);
            }

            if (related.PendingCount == 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Success, 0, 0);
            }

            SignalWorker();

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var remaining = timeout - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Timeout, related.PendingCount, 0);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds)),
                    timeProvider,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Timeout, related.PendingCount, 0);
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outbox.GetSnapshot(timeProvider.GetUtcNow()).PendingCount == 0)
            {
                return;
            }

            var processed = false;
            await processingGate.WaitAsync(cancellationToken);
            try
            {
                processed = await ProcessNextDueAsync(cancellationToken);
            }
            finally
            {
                processingGate.Release();
            }

            if (!processed)
            {
                await DelayUntilNextAttemptAsync(cancellationToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref acceptingRecords, 0);
        SignalWorker();
        try
        {
            await base.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Der lokale Completion-Worker wurde beim Shutdown abgebrochen; offene Aufträge bleiben dauerhaft vorgemerkt.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var processed = false;
                await processingGate.WaitAsync(stoppingToken);
                try
                {
                    processed = await ProcessNextDueAsync(stoppingToken);
                }
                finally
                {
                    processingGate.Release();
                }

                if (!processed)
                {
                    await WaitForWorkAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ProcessNextDueAsync(CancellationToken cancellationToken)
    {
        var item = await outbox.ReadNextDueAsync(timeProvider.GetUtcNow(), cancellationToken);
        if (item is null)
        {
            return false;
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var record = LiveRoomCompletionOutbox.Deserialize(item);
            await completionWriter.PersistAsync(record, cancellationToken);
            await outbox.CompleteAsync(item, cancellationToken);
            Interlocked.Increment(ref persistedCompletions);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var attempt = checked(item.AttemptCount + 1);
            if (IsTransientSqliteFailure(exception))
            {
                var delay = CalculateRetryDelay(attempt);
                Interlocked.Increment(ref retryAttempts);
                logger.LogWarning(
                    exception,
                    "Transientes SQLite-Problem bei der Arena-Persistenz; Versuch {Attempt} wird nach {Delay} erneut ausgeführt.",
                    attempt,
                    delay);
                await outbox.RescheduleAsync(
                    item,
                    attempt,
                    timeProvider.GetUtcNow().Add(delay),
                    exception.Message,
                    CancellationToken.None);
                return true;
            }

            logger.LogError(
                exception,
                "Ein Arena-Ergebnis konnte nicht persistiert werden; der dauerhaft gesicherte Auftrag wartet auf Redrive.");
            await outbox.FailAsync(item, attempt, exception.Message, CancellationToken.None);
            Interlocked.Increment(ref failedCompletions);
            return true;
        }
        finally
        {
            Interlocked.Add(ref totalPersistenceStopwatchTicks, Stopwatch.GetTimestamp() - startedAt);
            Interlocked.Increment(ref measuredPersistenceOperations);
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        var nextAttempt = outbox.GetNextAttemptAt();
        var delay = nextAttempt is null
            ? TimeSpan.FromSeconds(1)
            : nextAttempt.Value - timeProvider.GetUtcNow();
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 1, 1_000));
        await wakeSignal.WaitAsync(delay, cancellationToken);
    }

    private async Task DelayUntilNextAttemptAsync(CancellationToken cancellationToken)
    {
        var nextAttempt = outbox.GetNextAttemptAt();
        var delay = nextAttempt is null
            ? TimeSpan.FromMilliseconds(100)
            : nextAttempt.Value - timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private void SignalWorker()
    {
        if (wakeSignal.CurrentCount == 0)
        {
            wakeSignal.Release();
        }
    }

    private static TimeSpan CalculateRetryDelay(int attempt)
    {
        var exponent = Math.Min(Math.Max(0, attempt - 1), 8);
        return TimeSpan.FromMilliseconds(Math.Min(30_000, 100 * (1 << exponent)));
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

    private static void ValidateOptions(LiveOptions options)
    {
        var failures = new List<string>();
        if (options.MaxConcurrentRooms is < 1 or > 65_536)
        {
            failures.Add("MaxConcurrentRooms muss zwischen 1 und 65536 liegen.");
        }

        if (options.MaxActiveRoomsPerCreator is < 1 or > 1_024)
        {
            failures.Add("MaxActiveRoomsPerCreator muss zwischen 1 und 1024 liegen.");
        }

        if (options.CompletionQueueCapacity is < 1 or > 65_536)
        {
            failures.Add("CompletionQueueCapacity muss zwischen 1 und 65536 liegen.");
        }
        else if (options.CompletionQueueCapacity < options.MaxConcurrentRooms)
        {
            failures.Add("CompletionQueueCapacity muss mindestens MaxConcurrentRooms entsprechen.");
        }

        if (options.CompletionDrainTimeoutSeconds is < 1 or > 300)
        {
            failures.Add("CompletionDrainTimeoutSeconds muss zwischen 1 und 300 liegen.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(nameof(LiveOptions), typeof(LiveOptions), failures);
        }
    }

}

public sealed class RelationalLiveRoomCompletionWriter(IServiceScopeFactory scopeFactory) : ILiveRoomCompletionWriter
{
    public async Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        await using var strategyScope = scopeFactory.CreateAsyncScope();
        var strategyDb = strategyScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => PersistOnceAsync(record, cancellationToken));
    }

    private async Task PersistOnceAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var motivation = scope.ServiceProvider.GetRequiredService<MotivationService>();
        await using var sqliteSeasonWriteFence = db.Database.IsSqlite()
            ? await SeasonWriteFence.AcquireAsync(db, cancellationToken)
            : null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var postgresSeasonWriteFence = db.Database.IsNpgsql()
            ? await SeasonWriteFence.AcquireAsync(db, cancellationToken)
            : null;
        var participantIds = record.Participants.Select(item => item.UserProfileId).Distinct().ToArray();
        await ProfileWriteFence.AcquireAsync(db, participantIds, cancellationToken);
        if (await db.LiveRoomSummaries.AnyAsync(item => item.Id == record.Id || item.IdempotencyKey == record.IdempotencyKey, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var profiles = await db.UserProfiles
            .Where(item => participantIds.Contains(item.Id) && !item.Deleted)
            .ToListAsync(cancellationToken);
        if (profiles.Count != participantIds.Length)
        {
            throw new InvalidOperationException("Mindestens ein Arena-Teilnehmerprofil fehlt oder ist gelöscht; das Ergebnis wird nicht gewertet.");
        }

        var profilesById = profiles.ToDictionary(profile => profile.Id);
        var participantsById = record.Participants.ToDictionary(participant => participant.UserProfileId);
        var rewardEligibility = record.Participants.ToDictionary(
            participant => participant.UserProfileId,
            participant => record.TargetCompetitionEligible &&
                participant.Status == ParticipantStatus.Finished &&
                participant.CompetitionEligible &&
                CompetitionEligibility.HasPlausibleServerPace(
                    participant.CorrectCharacters,
                    TimeSpan.FromMilliseconds(participant.DurationMilliseconds)));
        var ratingEligibility = record.Participants.ToDictionary(
            participant => participant.UserProfileId,
            participant => participant.CompetitionEligible &&
                (participant.Status == ParticipantStatus.Dnf ||
                 participant.Status == ParticipantStatus.Finished &&
                 CompetitionEligibility.HasPlausibleServerPace(
                     participant.CorrectCharacters,
                     TimeSpan.FromMilliseconds(participant.DurationMilliseconds))));
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
        var ranked = record.Participants.All(item => item.Placement is not null)
            ? rankingInput
                .Select(result => new RankedRaceResult(
                    result,
                    record.Participants.Single(item => item.UserProfileId == result.UserProfileId).Placement!.Value))
                .OrderBy(item => item.Placement)
                .ThenBy(item => item.Result.UserProfileId)
                .ToArray()
            : RaceRanking.RankClassic(rankingInput);
        var isServerAbort = record.Participants.Any(item => item.Status == ParticipantStatus.AbortedByServer);
        var isClassicSingleRound = record.Mode == LiveRoomMode.Classic && record.RoundCount == 1;
        var validClassicFinishers = isClassicSingleRound
            ? RaceRanking.RankClassic(rankingInput
                .Where(result => rewardEligibility[result.UserProfileId])
                .ToArray())
            : [];
        var classicForfeitPlacement = validClassicFinishers.Count + 1;
        var ratingRanked = isClassicSingleRound
            ? validClassicFinishers
                .Concat(rankingInput
                    .Where(result =>
                    {
                        var participant = participantsById[result.UserProfileId];
                        return participant.Status == ParticipantStatus.Dnf && participant.CompetitionEligible ||
                            participant.Status == ParticipantStatus.Finished && !rewardEligibility[result.UserProfileId];
                    })
                    .Select(result => new RankedRaceResult(
                        result with { Status = ParticipantStatus.Dnf },
                        classicForfeitPlacement)))
                .OrderBy(item => item.Placement)
                .ThenBy(item => item.Result.UserProfileId)
                .ToArray()
            : ranked
                .Where(item => ratingEligibility[item.Result.UserProfileId])
                .ToArray();
        var ratingProfileIds = ratingRanked
            .Select(item => item.Result.UserProfileId)
            .ToHashSet();
        var ratingRequiresFullFieldIntegrity = record.RoundCount > 1 || record.Mode == LiveRoomMode.Team;
        var ratingFieldEligible = !ratingRequiresFullFieldIntegrity || record.Participants.All(participant =>
            ratingEligibility[participant.UserProfileId]);
        IReadOnlyDictionary<Guid, int?>? ratingTeamNumbers = null;
        if (record.Mode == LiveRoomMode.Team)
        {
            ratingTeamNumbers = record.Participants
                .Where(participant => ratingProfileIds.Contains(participant.UserProfileId))
                .ToDictionary(participant => participant.UserProfileId, participant => participant.TeamNumber);
            var teamSizes = ratingTeamNumbers.Values
                .Where(teamNumber => teamNumber is 1 or 2)
                .GroupBy(teamNumber => teamNumber!.Value)
                .ToDictionary(group => group.Key, group => group.Count());
            ratingFieldEligible &= teamSizes.Count == 2 &&
                teamSizes.GetValueOrDefault(1) > 0 &&
                teamSizes.GetValueOrDefault(1) == teamSizes.GetValueOrDefault(2) &&
                teamSizes.Values.Sum() == ratingTeamNumbers.Count;
        }

        var hasRatedFinisher = isClassicSingleRound
            ? validClassicFinishers.Count > 0
            : ratingRanked.Any(item => item.Result.Status == ParticipantStatus.Finished);
        if (record.TargetCompetitionEligible && record.RatingEligible && !isServerAbort &&
            ratingFieldEligible && hasRatedFinisher && ratingRanked.Length >= 2)
        {
            var ratings = profiles
                .Where(item => ratingProfileIds.Contains(item.Id))
                .ToDictionary(item => item.Id, item => item.ArenaRating);
            foreach (var ratingChange in MultiplayerRating.CalculatePairwiseEloChanges(
                         ratings,
                         ratingRanked,
                         teamNumbers: ratingTeamNumbers))
            {
                ratingChanges[ratingChange.Key] = ratingChange.Value;
            }

            foreach (var profile in profiles.Where(item => ratingProfileIds.Contains(item.Id)))
            {
                var ratingChange = ratingChanges[profile.Id];
                profile.ArenaRating = ratingChange.RatingAfter;
                profile.RatedMatchCount++;
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
            TargetTextHash = record.TargetTextHash,
            Mode = record.Mode,
            Visibility = record.Visibility,
            RoundCount = record.RoundCount,
            CreatedAt = record.CreatedAt,
            StartedAt = record.StartedAt,
            FinishedAt = record.FinishedAt,
            AbortedByServer = isServerAbort
        });

        var motivationInputs = new List<ArenaMotivationInput>(record.Participants.Count);
        foreach (var participant in record.Participants)
        {
            var ratingChange = ratingChanges[participant.UserProfileId];
            db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
            {
                LiveRoomSummaryId = record.Id,
                UserProfileId = participant.UserProfileId,
                TeamNumber = participant.TeamNumber,
                Status = participant.Status,
                Placement = participant.Placement,
                DurationMilliseconds = participant.DurationMilliseconds,
                Wpm = participant.Wpm,
                Accuracy = participant.Accuracy,
                CompetitionEligible = rewardEligibility[participant.UserProfileId],
                RatingBefore = ratingChange.RatingBefore,
                RatingDelta = ratingChange.RatingDelta,
                RatingAfter = ratingChange.RatingAfter
            });

            if (!isServerAbort && rewardEligibility[participant.UserProfileId])
            {
                motivationInputs.Add(new ArenaMotivationInput(
                    profilesById[participant.UserProfileId],
                    $"{record.IdempotencyKey}:{participant.UserProfileId:N}",
                    participant.Wpm,
                    participant.Accuracy,
                    participant.DurationMilliseconds,
                    record.FinishedAt ?? record.CreatedAt));
            }
        }

        await motivation.ApplyArenaResultsAsync(motivationInputs, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
