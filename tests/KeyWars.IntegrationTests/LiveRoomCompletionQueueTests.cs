using System.Data.Common;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class LiveRoomCompletionQueueTests
{
    [Fact]
    public async Task FlushPersistsRoomResultsAndRatingExactlyOnce()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var first = context.FirstProfileId;
        var second = context.SecondProfileId;
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, first, second);

        var firstReceipt = context.Queue.Enqueue(record);
        var duplicateReceipt = context.Queue.Enqueue(record);
        Assert.Equal(CompletionState.Pending, firstReceipt.State);
        Assert.Equal(firstReceipt, duplicateReceipt);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var room = await db.LiveRoomSummaries.SingleAsync();
        var participants = await db.LiveRoomParticipantSummaries.OrderBy(item => item.Placement).ToListAsync();
        var profiles = await db.UserProfiles.OrderBy(item => item.DisplayName).ToListAsync();

        Assert.Equal(record.Id, room.Id);
        Assert.Equal(record.IdempotencyKey, room.IdempotencyKey);
        Assert.Equal(1, room.RoundNumber);
        Assert.Equal(2, participants.Count);
        Assert.Equal(1, profiles[0].RatedMatchCount);
        Assert.Equal(1, profiles[1].RatedMatchCount);
        Assert.True(profiles.Single(item => item.Id == first).ArenaRating > 1000);
        Assert.True(profiles.Single(item => item.Id == second).ArenaRating < 1000);
        var firstProfile = profiles.Single(item => item.Id == first);
        var secondProfile = profiles.Single(item => item.Id == second);
        var firstSummary = participants.Single(item => item.UserProfileId == first);
        var secondSummary = participants.Single(item => item.UserProfileId == second);
        Assert.Equal(1000, firstSummary.RatingBefore);
        Assert.Equal(firstProfile.ArenaRating, firstSummary.RatingAfter);
        Assert.Equal(firstProfile.ArenaRating - firstSummary.RatingBefore, firstSummary.RatingDelta);
        Assert.Equal(1000, secondSummary.RatingBefore);
        Assert.Equal(secondProfile.ArenaRating, secondSummary.RatingAfter);
        Assert.Equal(secondProfile.ArenaRating - secondSummary.RatingBefore, secondSummary.RatingDelta);
        Assert.Equal(2, await db.RewardLedgerEntries.CountAsync(item => item.Source == "arena"));
        Assert.Equal(2, await db.GamificationEvents.CountAsync(item => item.Type == GamificationEventType.ArenaResult));
        Assert.Contains(await db.Missions.ToListAsync(), item => item.UserProfileId == first && item.Key == "daily-arena-or-team" && item.Completed);
        Assert.Equal(CompletionState.Persisted, context.Queue.GetStatus(roomId).State);
        Assert.Equal(0, context.Queue.PendingCount);
    }

    [Fact]
    public async Task SqliteWriterIsIdempotentAcrossFreshScopes()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var record = CreateRecord(Guid.CreateVersion7(), context.ProfileIds);

        await context.Writer.PersistAsync(record, CancellationToken.None);
        var firstState = await ReadWriterStateAsync(context.Services);
        await context.Writer.PersistAsync(record, CancellationToken.None);
        var secondState = await ReadWriterStateAsync(context.Services);

        Assert.Equal(1, firstState.RoomCount);
        Assert.Equal(4, firstState.ParticipantCount);
        Assert.Equal(4, firstState.Ledgers.Count(item => item.Source == "arena"));
        Assert.All(firstState.Profiles, profile => Assert.Equal(1, profile.RatedMatchCount));
        var profilesById = firstState.Profiles.ToDictionary(profile => profile.Id);
        for (var index = 0; index < context.ProfileIds.Count; index++)
        {
            var profileId = context.ProfileIds[index];
            var profile = profilesById[profileId];
            Assert.Equal(175 - index, profile.ExperiencePoints);
            Assert.Equal(8, profile.SeasonPoints);
            Assert.Equal(
                115 - index,
                Assert.Single(firstState.Ledgers, item =>
                    item.UserProfileId == profileId && item.Source == "arena").Xp);
            Assert.Equal(
                profile.ExperiencePoints,
                firstState.Ledgers.Where(item => item.UserProfileId == profileId).Sum(item => item.Xp));
        }

        Assert.Equal(firstState.Profiles, secondState.Profiles);
        Assert.Equal(firstState.Missions, secondState.Missions);
        Assert.Equal(firstState.Ledgers, secondState.Ledgers);
        Assert.Equal(firstState.Achievements, secondState.Achievements);
        Assert.Equal(firstState.Events, secondState.Events);
    }

    [Fact]
    public async Task ArenaPersistenceUsesParticipantIndependentReadCountAndOneSave()
    {
        var twoParticipantReads = await PersistAndMeasureAsync(2);
        var sixtyFourParticipantReads = await PersistAndMeasureAsync(64);

        Assert.Equal(twoParticipantReads, sixtyFourParticipantReads);
        Assert.Equal(13, sixtyFourParticipantReads);
    }

    [Fact]
    public async Task SqliteWriterRollsBackEveryEffectWhenFinalSaveFails()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var record = CreateRecord(Guid.CreateVersion7(), context.ProfileIds);
        context.SaveProbe.ArmFailureAfterSave();

        await Assert.ThrowsAsync<InjectedSaveFailureException>(() =>
            context.Writer.PersistAsync(record, CancellationToken.None));

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            Assert.Empty(await db.LiveRoomSummaries.ToListAsync());
            Assert.Empty(await db.LiveRoomParticipantSummaries.ToListAsync());
            Assert.Empty(await db.RewardLedgerEntries.ToListAsync());
            Assert.Empty(await db.Missions.ToListAsync());
            Assert.Empty(await db.Achievements.ToListAsync());
            Assert.Empty(await db.GamificationEvents.ToListAsync());
            Assert.All(await db.UserProfiles.ToListAsync(), profile =>
            {
                Assert.Equal(1000, profile.ArenaRating);
                Assert.Equal(0, profile.RatedMatchCount);
                Assert.Equal(0, profile.SeasonPoints);
                Assert.Equal(0, profile.ExperiencePoints);
                Assert.Equal(1, profile.Level);
                Assert.Equal(0, profile.CurrentStreakDays);
                Assert.Null(profile.LastActivityDate);
            });
        }

        context.SaveProbe.Reset();
        await context.Writer.PersistAsync(record, CancellationToken.None);
        Assert.Equal(1, context.SaveProbe.SavingChangesCount);
        var persisted = await ReadWriterStateAsync(context.Services);
        Assert.Equal(1, persisted.RoomCount);
        Assert.Equal(4, persisted.ParticipantCount);
        Assert.Equal(4, persisted.Ledgers.Count(item => item.Source == "arena"));
        Assert.All(persisted.Profiles, profile => Assert.Equal(1, profile.RatedMatchCount));
    }

    private static async Task<int> PersistAndMeasureAsync(int participantCount)
    {
        await using var context = await WriterTestContext.CreateAsync(participantCount);
        var record = CreateRecord(Guid.CreateVersion7(), context.ProfileIds);
        context.QueryCounter.Reset();
        context.SaveProbe.Reset();

        await context.Writer.PersistAsync(record, CancellationToken.None);

        Assert.Equal(1, context.SaveProbe.SavingChangesCount);
        return context.QueryCounter.LinqReadCount;
    }

    private static async Task<WriterPersistenceState> ReadWriterStateAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = (await db.UserProfiles.AsNoTracking().OrderBy(item => item.Id).ToListAsync())
            .Select(item => new ProfilePersistenceState(
                item.Id,
                item.ArenaRating,
                item.RatedMatchCount,
                item.SeasonPoints,
                item.ExperiencePoints,
                item.Level,
                item.CurrentStreakDays,
                item.LastActivityDate))
            .ToArray();
        var missions = (await db.Missions
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.MissionDate)
                .ThenBy(item => item.Key)
                .ToListAsync())
            .Select(item => new MissionPersistenceState(
                item.UserProfileId,
                item.MissionDate,
                item.Key,
                item.CurrentValue,
                item.Completed))
            .ToArray();
        var ledgers = (await db.RewardLedgerEntries
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.Source)
                .ThenBy(item => item.SourceId)
                .ToListAsync())
            .Select(item => new LedgerPersistenceState(
                item.UserProfileId,
                item.Source,
                item.SourceId,
                item.Xp))
            .ToArray();
        var achievements = (await db.Achievements
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.Key)
                .ToListAsync())
            .Select(item => new AchievementPersistenceState(item.UserProfileId, item.Key))
            .ToArray();
        var events = (await db.GamificationEvents
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.Source)
                .ThenBy(item => item.SourceId)
                .ThenBy(item => item.EventKey)
                .ToListAsync())
            .Select(item => new EventPersistenceState(
                item.UserProfileId,
                item.Source,
                item.SourceId,
                item.EventKey,
                item.Type,
                item.XpDelta))
            .ToArray();

        return new WriterPersistenceState(
            await db.LiveRoomSummaries.CountAsync(),
            await db.LiveRoomParticipantSummaries.CountAsync(),
            profiles,
            missions,
            ledgers,
            achievements,
            events);
    }

    [Fact]
    public async Task StopAsyncFlushesQueuedCompletions()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);

        context.Queue.Enqueue(record);
        await context.Queue.StopAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync());
        Assert.Equal(0, context.Queue.PendingCount);
    }

    [Fact]
    public async Task ServerAbortPersistsWithoutRatingChange()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId, abortedByServer: true);

        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var room = await db.LiveRoomSummaries.SingleAsync();
        var participants = await db.LiveRoomParticipantSummaries.ToListAsync();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.True(room.AbortedByServer);
        Assert.All(participants, participant => Assert.Equal(0, participant.RatingDelta));
        Assert.All(participants, participant => Assert.Equal(participant.RatingBefore, participant.RatingAfter));
        Assert.All(profiles, profile => Assert.Equal(1000, profile.ArenaRating));
        Assert.All(profiles, profile => Assert.Equal(0, profile.RatedMatchCount));
        Assert.Empty(await db.GamificationEvents.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRoomCompletionsUpdateRatingsOncePerRoom()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var firstRecord = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);
        var secondRecord = CreateRecord(Guid.CreateVersion7(), context.SecondProfileId, context.FirstProfileId);

        await Task.WhenAll(
            Task.Run(() => context.Queue.Enqueue(firstRecord)),
            Task.Run(() => context.Queue.Enqueue(secondRecord)));
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.Equal(2, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(4, await db.LiveRoomParticipantSummaries.CountAsync());
        Assert.All(profiles, profile => Assert.Equal(2, profile.RatedMatchCount));
    }

    [Fact]
    public async Task FlushRetriesAfterTransientSqliteFailure()
    {
        await using var context = await CompletionTestContext.CreateAsync(transientFailureOnFirstWrite: true);
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);

        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Equal(2, context.WriterAttempts);
        Assert.Equal(1, context.Queue.RetryAttempts);
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync());
    }

    [Fact]
    public async Task PermanentFailureRemainsFailedWithoutRatingOrRewards()
    {
        await using var context = await CompletionTestContext.CreateAsync(permanentFailure: true);
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, context.FirstProfileId, context.SecondProfileId);

        var receipt = context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);
        await context.Queue.FlushAsync(CancellationToken.None);
        var drain = await context.Queue.DrainProfileAsync(context.FirstProfileId);

        Assert.Equal(CompletionState.Pending, receipt.State);
        Assert.Equal(CompletionState.Failed, context.Queue.GetStatus(roomId).State);
        Assert.Equal(CompletionDrainStatus.Failed, drain.Status);
        Assert.Equal(1, context.Queue.FailedRecordCount);
        Assert.Equal(1, context.Queue.GetMetrics().FailedCompletions);
        Assert.Equal(3, context.WriterAttempts);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Empty(await db.LiveRoomSummaries.ToListAsync());
        Assert.Empty(await db.LiveRoomParticipantSummaries.ToListAsync());
        Assert.Empty(await db.RewardLedgerEntries.ToListAsync());
        Assert.All(await db.UserProfiles.ToListAsync(), profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
        });
    }

    [Fact]
    public async Task DrainTimesOutWhileRelatedCompletionIsPending()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);
        context.Queue.Enqueue(record);

        var result = await context.Queue.DrainProfileAsync(
            context.FirstProfileId,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(CompletionDrainStatus.Timeout, result.Status);
        Assert.Equal(1, result.PendingJobs);
    }

    [Fact]
    public async Task DrainWaitsForRuntimePersistenceAndReturnsSuccess()
    {
        var queue = new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions { MaxConcurrentRooms = 1, CompletionQueueCapacity = 1 }),
            new NoopCompletionWriter(),
            NullLogger<LiveRoomCompletionQueue>.Instance);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, first, second);
        await queue.StartAsync(CancellationToken.None);
        queue.Enqueue(record);

        var result = await queue.DrainProfileAsync(
            first,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(CompletionDrainStatus.Success, result.Status);
        Assert.Equal(CompletionState.Persisted, queue.GetStatus(roomId).State);
        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void FullQueueReturnsFailedReceiptAndDoesNotReportPending()
    {
        var queue = new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions { MaxConcurrentRooms = 1, CompletionQueueCapacity = 1 }),
            new NoopCompletionWriter(),
            NullLogger<LiveRoomCompletionQueue>.Instance);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var firstRecord = CreateRecord(Guid.CreateVersion7(), first, second);
        var rejectedRecord = CreateRecord(Guid.CreateVersion7(), first, second);

        var pending = queue.Enqueue(firstRecord);
        var rejected = queue.Enqueue(rejectedRecord);

        Assert.Equal(CompletionState.Pending, pending.State);
        Assert.Equal(CompletionState.Failed, rejected.State);
        Assert.Equal(CompletionState.Failed, queue.GetStatus(rejectedRecord.Id).State);
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.FailedRecordCount);
        Assert.False(queue.CanAcceptNewRoom(0));
    }

    [Fact]
    public void QueueRejectsCapacityBelowMaximumConcurrentRooms()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions { MaxConcurrentRooms = 2, CompletionQueueCapacity = 1 }),
            new NoopCompletionWriter(),
            NullLogger<LiveRoomCompletionQueue>.Instance));

        Assert.Contains("mindestens MaxConcurrentRooms", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueRejectsInvalidProfileDrainTimeout()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions
            {
                MaxConcurrentRooms = 1,
                CompletionQueueCapacity = 1,
                CompletionDrainTimeoutSeconds = 0
            }),
            new NoopCompletionWriter(),
            NullLogger<LiveRoomCompletionQueue>.Instance));

        Assert.Contains("CompletionDrainTimeoutSeconds", exception.Message, StringComparison.Ordinal);
    }

    private static CompletedRoomRecord CreateRecord(Guid roomId, Guid first, Guid second, bool abortedByServer = false)
        => CreateRecord(roomId, [first, second], abortedByServer);

    private static CompletedRoomRecord CreateRecord(
        Guid roomId,
        IReadOnlyList<Guid> profileIds,
        bool abortedByServer = false)
    {
        var createdAt = DateTimeOffset.Parse("2026-06-18T12:00:00Z");
        return new CompletedRoomRecord(
            roomId,
            1,
            2,
            $"{roomId:N}:1:2",
            profileIds[0],
            "ABC123",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            createdAt,
            createdAt.AddSeconds(3),
            createdAt.AddSeconds(35),
            profileIds
                .Select((profileId, index) => new CompletedParticipantRecord(
                    profileId,
                    abortedByServer ? ParticipantStatus.AbortedByServer : ParticipantStatus.Finished,
                    abortedByServer ? null : index + 1,
                    32_000 + (index * 100),
                    Math.Max(20, 80 - index),
                    100))
                .ToList());
    }

    private sealed class WriterTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private WriterTestContext(
            SqliteConnection connection,
            ServiceProvider services,
            RelationalLiveRoomCompletionWriter writer,
            IReadOnlyList<Guid> profileIds,
            SelectCommandCounter queryCounter,
            SaveChangesProbe saveProbe)
        {
            this.connection = connection;
            Services = services;
            Writer = writer;
            ProfileIds = profileIds;
            QueryCounter = queryCounter;
            SaveProbe = saveProbe;
        }

        public ServiceProvider Services { get; }
        public RelationalLiveRoomCompletionWriter Writer { get; }
        public IReadOnlyList<Guid> ProfileIds { get; }
        public SelectCommandCounter QueryCounter { get; }
        public SaveChangesProbe SaveProbe { get; }

        public static async Task<WriterTestContext> CreateAsync(int participantCount)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var queryCounter = new SelectCommandCounter();
            var saveProbe = new SaveChangesProbe();
            var services = new ServiceCollection();
            services.AddDbContext<KeyWarsDbContext>(options =>
                options
                    .UseSqlite(connection)
                    .AddInterceptors(queryCounter, saveProbe));
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.Parse("2026-06-18T12:02:00Z")));
            services.AddScoped<MotivationService>();
            services.AddSingleton<RelationalLiveRoomCompletionWriter>();
            var provider = services.BuildServiceProvider();

            var profileIds = new List<Guid>(participantCount);
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                await db.Database.EnsureCreatedAsync();
                var profiles = Enumerable.Range(1, participantCount)
                    .Select(index => new UserProfile
                    {
                        DisplayName = $"Arena Test {index}",
                        SamAccountName = $"arena-test-{index}",
                        DirectoryObjectGuid = Guid.NewGuid().ToString(),
                        DirectorySid = $"S-1-5-21-{index}"
                    })
                    .ToList();
                db.UserProfiles.AddRange(profiles);
                await db.SaveChangesAsync();
                profileIds.AddRange(profiles.Select(profile => profile.Id));
            }

            queryCounter.Reset();
            saveProbe.Reset();
            return new WriterTestContext(
                connection,
                provider,
                provider.GetRequiredService<RelationalLiveRoomCompletionWriter>(),
                profileIds,
                queryCounter,
                saveProbe);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        private int linqReadCount;

        public int LinqReadCount => Volatile.Read(ref linqReadCount);

        public void Reset() => Interlocked.Exchange(ref linqReadCount, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CountLinqRead(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountLinqRead(eventData);
            return ValueTask.FromResult(result);
        }

        private void CountLinqRead(CommandEventData eventData)
        {
            if (eventData.CommandSource == CommandSource.LinqQuery)
            {
                Interlocked.Increment(ref linqReadCount);
            }
        }
    }

    private sealed class SaveChangesProbe : SaveChangesInterceptor
    {
        private int failAfterSave;
        private int savingChangesCount;

        public int SavingChangesCount => Volatile.Read(ref savingChangesCount);

        public void Reset()
        {
            Interlocked.Exchange(ref failAfterSave, 0);
            Interlocked.Exchange(ref savingChangesCount, 0);
        }

        public void ArmFailureAfterSave() => Interlocked.Exchange(ref failAfterSave, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref savingChangesCount);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref failAfterSave, 0) == 1)
            {
                throw new InjectedSaveFailureException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedSaveFailureException : Exception;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record WriterPersistenceState(
        int RoomCount,
        int ParticipantCount,
        ProfilePersistenceState[] Profiles,
        MissionPersistenceState[] Missions,
        LedgerPersistenceState[] Ledgers,
        AchievementPersistenceState[] Achievements,
        EventPersistenceState[] Events);

    private sealed record ProfilePersistenceState(
        Guid Id,
        int ArenaRating,
        int RatedMatchCount,
        int SeasonPoints,
        int ExperiencePoints,
        int Level,
        int CurrentStreakDays,
        DateOnly? LastActivityDate);

    private sealed record MissionPersistenceState(
        Guid UserProfileId,
        DateOnly MissionDate,
        string Key,
        int CurrentValue,
        bool Completed);

    private sealed record LedgerPersistenceState(
        Guid UserProfileId,
        string Source,
        string SourceId,
        int Xp);

    private sealed record AchievementPersistenceState(Guid UserProfileId, string Key);

    private sealed record EventPersistenceState(
        Guid UserProfileId,
        string Source,
        string SourceId,
        string EventKey,
        GamificationEventType Type,
        int XpDelta);

    private sealed class CompletionTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CompletionTestContext(SqliteConnection connection, ServiceProvider services, LiveRoomCompletionQueue queue, Guid firstProfileId, Guid secondProfileId)
        {
            this.connection = connection;
            Services = services;
            Queue = queue;
            FirstProfileId = firstProfileId;
            SecondProfileId = secondProfileId;
        }

        public ServiceProvider Services { get; }
        public LiveRoomCompletionQueue Queue { get; }
        public Guid FirstProfileId { get; }
        public Guid SecondProfileId { get; }
        public int WriterAttempts => flakyWriter?.Attempts ?? 0;

        private FlakyCompletionWriter? flakyWriter;

        public static async Task<CompletionTestContext> CreateAsync(bool transientFailureOnFirstWrite = false, bool permanentFailure = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<KeyWarsDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton(Options.Create(new LiveOptions { MaxConcurrentRooms = 16, CompletionQueueCapacity = 16 }));
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddScoped<MotivationService>();
            services.AddSingleton<RelationalLiveRoomCompletionWriter>();
            FlakyCompletionWriter? flakyWriter = null;
            if (transientFailureOnFirstWrite || permanentFailure)
            {
                services.AddSingleton<ILiveRoomCompletionWriter>(provider =>
                {
                    flakyWriter = new FlakyCompletionWriter(provider.GetRequiredService<RelationalLiveRoomCompletionWriter>(), permanentFailure);
                    return flakyWriter;
                });
            }
            else
            {
                services.AddSingleton<ILiveRoomCompletionWriter>(provider => provider.GetRequiredService<RelationalLiveRoomCompletionWriter>());
            }

            services.AddSingleton<LiveRoomCompletionQueue>();
            services.AddSingleton<ILiveRoomCompletionSink>(provider => provider.GetRequiredService<LiveRoomCompletionQueue>());
            services.AddSingleton<ILogger<LiveRoomCompletionQueue>>(NullLogger<LiveRoomCompletionQueue>.Instance);
            var provider = services.BuildServiceProvider();

            Guid first;
            Guid second;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                await db.Database.EnsureCreatedAsync();
                var firstProfile = new UserProfile
                {
                    DisplayName = "Anna Arena",
                    SamAccountName = "aarena",
                    DirectoryObjectGuid = Guid.NewGuid().ToString(),
                    DirectorySid = "S-1"
                };
                var secondProfile = new UserProfile
                {
                    DisplayName = "Bernd Arena",
                    SamAccountName = "barena",
                    DirectoryObjectGuid = Guid.NewGuid().ToString(),
                    DirectorySid = "S-2"
                };
                db.UserProfiles.AddRange(firstProfile, secondProfile);
                await db.SaveChangesAsync();
                first = firstProfile.Id;
                second = secondProfile.Id;
            }

            var queue = provider.GetRequiredService<LiveRoomCompletionQueue>();
            return new CompletionTestContext(connection, provider, queue, first, second)
            {
                flakyWriter = flakyWriter
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FlakyCompletionWriter(RelationalLiveRoomCompletionWriter inner, bool permanentFailure) : ILiveRoomCompletionWriter
    {
        public int Attempts { get; private set; }

        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
        {
            Attempts++;
            return permanentFailure || Attempts == 1
                ? Task.FromException(new SqliteException("database is locked", 5))
                : inner.PersistAsync(record, cancellationToken);
        }
    }

    private sealed class NoopCompletionWriter : ILiveRoomCompletionWriter
    {
        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
