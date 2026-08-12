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
    private const int MaxPersistenceAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)];
    private readonly ILiveRoomCompletionWriter completionWriter;
    private readonly ILogger<LiveRoomCompletionQueue> logger;
    private readonly Channel<CompletedRoomRecord> records;
    private readonly ConcurrentDictionary<Guid, CompletionTracker> trackedRecords = new();
    private readonly ConcurrentQueue<Guid> persistedRecordOrder = new();
    private readonly object enqueueGate = new();
    private readonly SemaphoreSlim processingGate = new(1, 1);
    private readonly TimeSpan defaultDrainTimeout;
    private int acceptingRecords = 1;
    private int pendingRecords;
    private int failedRecords;
    private int retainedPersistedRecords;
    private int queuedRecords;
    private long retryAttempts;
    private long persistedCompletions;
    private long failedCompletions;
    private long abortedUnconfirmedCompletions;
    private long measuredPersistenceOperations;
    private long totalPersistenceStopwatchTicks;

    public LiveRoomCompletionQueue(
        IOptions<LiveOptions> options,
        ILiveRoomCompletionWriter completionWriter,
        ILogger<LiveRoomCompletionQueue> logger)
    {
        var liveOptions = options.Value;
        ValidateOptions(liveOptions);
        this.completionWriter = completionWriter;
        this.logger = logger;
        Capacity = liveOptions.CompletionQueueCapacity;
        FailedRecordLimit = Capacity;
        defaultDrainTimeout = TimeSpan.FromSeconds(liveOptions.CompletionDrainTimeoutSeconds);
        records = Channel.CreateBounded<CompletedRoomRecord>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int Capacity { get; }
    public int FailedRecordLimit { get; }
    public int PendingCount => Volatile.Read(ref pendingRecords);
    public int FailedRecordCount => Volatile.Read(ref failedRecords);
    public long FailedAttempts => Volatile.Read(ref failedCompletions);
    public long RetryAttempts => Volatile.Read(ref retryAttempts);
    public TimeSpan OldestPendingAge => trackedRecords.Values
        .Where(tracker => tracker.State == CompletionState.Pending)
        .Select(tracker => tracker.Age)
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();

    public CompletionReceipt Enqueue(CompletedRoomRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.IdempotencyKey))
        {
            throw new InvalidOperationException("Arena-Abschlussdaten enthalten keinen Idempotenzschlüssel.");
        }

        lock (enqueueGate)
        {
            if (trackedRecords.TryGetValue(record.Id, out var existing))
            {
                if (!StringComparer.Ordinal.Equals(existing.IdempotencyKey, record.IdempotencyKey))
                {
                    throw new InvalidOperationException("Für diesen Arena-Raum existiert bereits ein anderer Persistenzauftrag.");
                }

                return existing.Receipt;
            }

            if (Volatile.Read(ref acceptingRecords) == 0 || PendingCount + FailedRecordCount >= Capacity)
            {
                return RegisterRejectedRecord(record);
            }

            var tracker = new CompletionTracker(record, CompletionState.Pending);
            if (!trackedRecords.TryAdd(record.Id, tracker))
            {
                throw new InvalidOperationException("Der Arena-Persistenzauftrag konnte nicht eindeutig registriert werden.");
            }

            Interlocked.Increment(ref pendingRecords);
            if (records.Writer.TryWrite(record))
            {
                Interlocked.Increment(ref queuedRecords);
                return tracker.Receipt;
            }

            Interlocked.Decrement(ref pendingRecords);
            tracker.TransitionFromPending(CompletionState.Failed);
            RegisterFailedTracker(tracker);
            return tracker.Receipt;
        }
    }

    public CompletionStatusSnapshot GetStatus(Guid roomId)
    {
        return trackedRecords.TryGetValue(roomId, out var tracker)
            ? new CompletionStatusSnapshot(tracker.State)
            : new CompletionStatusSnapshot(CompletionState.AbortedUnconfirmed);
    }

    public bool CanAcceptNewRoom(int currentRoomCount)
    {
        if (currentRoomCount < 0 ||
            Volatile.Read(ref acceptingRecords) == 0 ||
            FailedRecordCount >= FailedRecordLimit)
        {
            return false;
        }

        return currentRoomCount + PendingCount + FailedRecordCount < Capacity;
    }

    public LiveRoomCompletionMetrics GetMetrics()
    {
        var operations = Volatile.Read(ref measuredPersistenceOperations);
        var stopwatchTicks = Volatile.Read(ref totalPersistenceStopwatchTicks);
        var averageMilliseconds = operations == 0
            ? 0
            : Math.Round(stopwatchTicks * 1000d / Stopwatch.Frequency / operations, 2);
        return new LiveRoomCompletionMetrics(
            PendingCount,
            FailedRecordCount,
            RetryAttempts,
            Volatile.Read(ref persistedCompletions),
            Volatile.Read(ref failedCompletions),
            Volatile.Read(ref abortedUnconfirmedCompletions),
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
            var related = trackedRecords.Values
                .Where(tracker => tracker.ContainsProfile(profileId))
                .ToArray();
            var failed = related.Count(tracker => tracker.State is CompletionState.Failed or CompletionState.AbortedUnconfirmed);
            if (failed > 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Failed, 0, failed);
            }

            var pending = related.Where(tracker => tracker.State == CompletionState.Pending).ToArray();
            if (pending.Length == 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Success, 0, 0);
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var remaining = timeout - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Timeout, pending.Length, 0);
            }

            try
            {
                await Task.WhenAll(pending.Select(tracker => tracker.Completion)).WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                return new CompletionDrainResult(
                    CompletionDrainStatus.Timeout,
                    related.Count(tracker => tracker.State == CompletionState.Pending),
                    0);
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await processingGate.WaitAsync(cancellationToken);
            try
            {
                while (records.Reader.TryRead(out var record))
                {
                    Interlocked.Decrement(ref queuedRecords);
                    await ProcessRecordAsync(record, cancellationToken);
                }
            }
            finally
            {
                processingGate.Release();
            }

            var pending = trackedRecords.Values
                .Where(tracker => tracker.State == CompletionState.Pending)
                .Select(tracker => tracker.Completion)
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            if (Volatile.Read(ref queuedRecords) > 0)
            {
                continue;
            }

            await Task.WhenAll(pending).WaitAsync(cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref acceptingRecords, 0);
        records.Writer.TryComplete();
        try
        {
            await FlushAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Count} Arena-Ergebnisjobs sind beim Shutdown noch in Bearbeitung.", PendingCount);
            MarkPendingAsAbortedUnconfirmed();
        }

        if (PendingCount > 0)
        {
            logger.LogWarning("{Count} Arena-Ergebnisjobs konnten vor dem Shutdown nicht persistiert werden.", PendingCount);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await records.Reader.WaitToReadAsync(stoppingToken))
            {
                await processingGate.WaitAsync(stoppingToken);
                try
                {
                    if (!records.Reader.TryRead(out var record))
                    {
                        continue;
                    }

                    Interlocked.Decrement(ref queuedRecords);
                    await ProcessRecordAsync(record, stoppingToken);
                }
                finally
                {
                    processingGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            MarkPendingAsAbortedUnconfirmed();
        }
    }

    private CompletionReceipt RegisterRejectedRecord(CompletedRoomRecord record)
    {
        var tracker = new CompletionTracker(record, CompletionState.Failed);
        if (trackedRecords.TryAdd(record.Id, tracker))
        {
            RegisterFailedTracker(tracker);
        }
        else
        {
            Interlocked.Increment(ref failedCompletions);
        }

        logger.LogError("Ein Arena-Ergebnis konnte wegen erschöpfter Persistenzkapazität nicht eingereiht werden.");
        return tracker.Receipt;
    }

    private void RegisterFailedTracker(CompletionTracker tracker)
    {
        Interlocked.Increment(ref failedCompletions);
        if (!TryReserveFailedRecord())
        {
            trackedRecords.TryRemove(new KeyValuePair<Guid, CompletionTracker>(tracker.RoomId, tracker));
        }

        tracker.Complete();
    }

    private bool TryReserveFailedRecord()
    {
        while (true)
        {
            var current = Volatile.Read(ref failedRecords);
            if (current >= FailedRecordLimit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref failedRecords, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private async Task ProcessRecordAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        if (!trackedRecords.TryGetValue(record.Id, out var tracker) ||
            !StringComparer.Ordinal.Equals(tracker.IdempotencyKey, record.IdempotencyKey) ||
            tracker.State != CompletionState.Pending)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var persisted = await PersistWithRetryAsync(record, cancellationToken);
            CompletePendingTracker(tracker, persisted ? CompletionState.Persisted : CompletionState.Failed);
        }
        catch (OperationCanceledException)
        {
            CompletePendingTracker(tracker, CompletionState.AbortedUnconfirmed);
            throw;
        }
        finally
        {
            Interlocked.Add(ref totalPersistenceStopwatchTicks, Stopwatch.GetTimestamp() - startedAt);
            Interlocked.Increment(ref measuredPersistenceOperations);
        }
    }

    private void CompletePendingTracker(CompletionTracker tracker, CompletionState state)
    {
        if (!tracker.TransitionFromPending(state))
        {
            return;
        }

        Interlocked.Decrement(ref pendingRecords);
        switch (state)
        {
            case CompletionState.Persisted:
                tracker.Complete();
                Interlocked.Increment(ref persistedCompletions);
                Interlocked.Increment(ref retainedPersistedRecords);
                persistedRecordOrder.Enqueue(tracker.RoomId);
                TrimPersistedRecords();
                break;
            case CompletionState.Failed:
                RegisterFailedTracker(tracker);
                break;
            case CompletionState.AbortedUnconfirmed:
                Interlocked.Increment(ref abortedUnconfirmedCompletions);
                if (!TryReserveFailedRecord())
                {
                    trackedRecords.TryRemove(new KeyValuePair<Guid, CompletionTracker>(tracker.RoomId, tracker));
                }

                tracker.Complete();
                break;
        }
    }

    private void TrimPersistedRecords()
    {
        while (Volatile.Read(ref retainedPersistedRecords) > Capacity && persistedRecordOrder.TryDequeue(out var roomId))
        {
            if (trackedRecords.TryGetValue(roomId, out var tracker) &&
                tracker.State == CompletionState.Persisted &&
                trackedRecords.TryRemove(new KeyValuePair<Guid, CompletionTracker>(roomId, tracker)))
            {
                Interlocked.Decrement(ref retainedPersistedRecords);
            }
        }
    }

    private void MarkPendingAsAbortedUnconfirmed()
    {
        foreach (var tracker in trackedRecords.Values.Where(tracker => tracker.State == CompletionState.Pending))
        {
            CompletePendingTracker(tracker, CompletionState.AbortedUnconfirmed);
        }
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
                Interlocked.Increment(ref retryAttempts);
                logger.LogWarning(ex, "Transientes SQLite-Problem bei der Arena-Persistenz; Versuch {Attempt}/{MaxAttempts}.", attempt, MaxPersistenceAttempts);
                await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)], cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ein Arena-Ergebnis konnte nicht persistiert werden; der Job bleibt als fehlgeschlagen sichtbar.");
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

    private static void ValidateOptions(LiveOptions options)
    {
        var failures = new List<string>();
        if (options.MaxConcurrentRooms is < 1 or > 65_536)
        {
            failures.Add("MaxConcurrentRooms muss zwischen 1 und 65536 liegen.");
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

    private sealed class CompletionTracker(CompletedRoomRecord record, CompletionState initialState)
    {
        private readonly long enqueuedAt = Stopwatch.GetTimestamp();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid[] profileIds = record.Participants
            .Select(participant => participant.UserProfileId)
            .Distinct()
            .ToArray();
        private int state = (int)initialState;

        public Guid RoomId => receipt.RoomId;
        public string IdempotencyKey => receipt.IdempotencyKey;
        public CompletionState State => (CompletionState)Volatile.Read(ref state);
        public CompletionReceipt Receipt => receipt with { State = State };
        public Task Completion => completion.Task;
        public TimeSpan Age => Stopwatch.GetElapsedTime(enqueuedAt);

        private readonly CompletionReceipt receipt = new(record.Id, record.IdempotencyKey, initialState);

        public bool ContainsProfile(Guid profileId)
        {
            return profileIds.Contains(profileId);
        }

        public bool TransitionFromPending(CompletionState nextState)
        {
            return Interlocked.CompareExchange(ref state, (int)nextState, (int)CompletionState.Pending) == (int)CompletionState.Pending;
        }

        public void Complete()
        {
            completion.TrySetResult();
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
                RatingBefore = ratingChange.RatingBefore,
                RatingDelta = ratingChange.RatingDelta,
                RatingAfter = ratingChange.RatingAfter
            });

            if (!isServerAbort && participant.Status == ParticipantStatus.Finished)
            {
                motivationInputs.Add(new ArenaMotivationInput(
                    profilesById[participant.UserProfileId],
                    $"{record.IdempotencyKey}:{participant.UserProfileId:N}",
                    participant.Wpm,
                    participant.Accuracy,
                    participant.DurationMilliseconds));
            }
        }

        await motivation.ApplyArenaResultsAsync(motivationInputs, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
