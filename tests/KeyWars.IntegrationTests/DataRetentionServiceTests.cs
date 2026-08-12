using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KeyWars.IntegrationTests;

public sealed class DataRetentionServiceTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"keywars-retention-{Guid.NewGuid():N}");

    [Fact]
    public async Task DryRunAndApplyRespectProtectionAndBatchBoundaries()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var options = new RetentionOptions
        {
            BatchSize = 1,
            MaxBatchesPerRun = 1,
            StaleAttemptHours = 2,
            AbandonedAttemptRetentionDays = 90,
            SeenGamificationEventRetentionDays = 180,
            BackupRetentionDays = 30,
            MinimumBackupPairs = 1
        };
        await using var db = await CreateDatabaseAsync();
        var seeded = await SeedAsync(db, now);
        db.ChangeTracker.Clear();
        var challengeLocks = new RecordingChallengeLockProvider();
        var attemptSessions = new RecordingAttemptSessionStateStore();
        var service = CreateService(db, options, now, challengeLocks, attemptSessions);

        var dryRun = await service.RunAsync(dryRun: true);

        Assert.Equal(1, dryRun.StaleAttempts.Candidates);
        Assert.Equal(1, dryRun.ExpiredChallenges.Candidates);
        Assert.Equal(1, dryRun.AbandonedAttempts.Candidates);
        Assert.Equal(2, dryRun.SeenGamificationEvents.Candidates);
        Assert.Equal(0, dryRun.StaleAttempts.Affected);
        Assert.Equal(0, dryRun.ExpiredChallenges.Affected);
        Assert.Equal(0, dryRun.AbandonedAttempts.Affected);
        Assert.Equal(0, dryRun.SeenGamificationEvents.Affected);
        Assert.Equal(AttemptPhase.Prepared, await ReadAttemptPhaseAsync(db, seeded.StaleAttemptId));
        Assert.Equal(ChallengeStatus.Open, await ReadChallengeStatusAsync(db, seeded.DueChallengeId));
        Assert.True(await db.TypingAttempts.AsNoTracking().AnyAsync(item => item.Id == seeded.DeletableAttemptId));
        Assert.Empty(challengeLocks.AcquiredChallengeIds);
        Assert.Empty(attemptSessions.AcquiredAttemptIds);

        var applied = await service.RunAsync(dryRun: false);
        db.ChangeTracker.Clear();

        Assert.Equal(1, applied.StaleAttempts.Affected);
        Assert.Equal(1, applied.ExpiredChallenges.Affected);
        Assert.Equal(1, applied.AbandonedAttempts.Affected);
        Assert.Equal(1, applied.SeenGamificationEvents.Affected);
        Assert.Equal(1, applied.SeenGamificationEvents.Remaining);
        Assert.True(applied.SeenGamificationEvents.BatchLimitReached);
        Assert.Equal(AttemptPhase.Expired, await ReadAttemptPhaseAsync(db, seeded.StaleAttemptId));
        Assert.Equal(ChallengeStatus.Expired, await ReadChallengeStatusAsync(db, seeded.DueChallengeId));
        Assert.NotNull(await db.Challenges.AsNoTracking()
            .Where(item => item.Id == seeded.DueChallengeId)
            .Select(item => item.FinishedAt)
            .SingleAsync());
        Assert.False(await db.TypingAttempts.AsNoTracking().AnyAsync(item => item.Id == seeded.DeletableAttemptId));
        Assert.True(await db.TypingAttempts.AsNoTracking().AnyAsync(item => item.Id == seeded.LedgerProtectedAttemptId));
        Assert.True(await db.TypingAttempts.AsNoTracking().AnyAsync(item => item.Id == seeded.BindingProtectedAttemptId));
        Assert.Equal(AttemptPhase.Aborted, await ReadAttemptPhaseAsync(db, seeded.BindingProtectedAttemptId));
        Assert.Empty(await db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == seeded.BindingProtectedAttemptId).ToListAsync());
        Assert.True(await db.TypingAttempts.AsNoTracking().AnyAsync(item => item.Id == seeded.CompletedAttemptId));
        Assert.True(await db.GamificationEvents.AsNoTracking().AnyAsync(item => item.Id == seeded.UnseenEventId));
        Assert.Single(await db.RewardLedgerEntries.AsNoTracking().ToListAsync());
        Assert.Contains(nameof(KeyWarsDbContext.RewardLedgerEntries), applied.ProtectedDataSets);
        Assert.Equal([seeded.DueChallengeId], challengeLocks.AcquiredChallengeIds);
        Assert.Contains(seeded.StaleAttemptId, attemptSessions.AcquiredAttemptIds);
    }

    [Fact]
    public async Task AttemptExpirationDoesNotMutateBeforeItsLifecycleFenceIsAcquired()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        await using var db = await CreateDatabaseAsync();
        var seeded = await SeedAsync(db, now);
        db.ChangeTracker.Clear();
        var attemptSessions = new RecordingAttemptSessionStateStore(rejectAcquisition: true);
        var service = CreateService(
            db,
            new RetentionOptions(),
            now,
            attemptSessions: attemptSessions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunAsync(dryRun: false));

        Assert.Equal("Attempt-Fence nicht verfügbar.", exception.Message);
        Assert.Equal([seeded.StaleAttemptId], attemptSessions.AcquiredAttemptIds);
        db.ChangeTracker.Clear();
        var attempt = await db.TypingAttempts.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.StaleAttemptId);
        Assert.Equal(AttemptPhase.Prepared, attempt.Phase);
        Assert.Null(attempt.FinishedAt);
    }

    [Fact]
    public async Task ChallengeExpirationDoesNotMutateBeforeItsFenceIsAcquired()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        await using var db = await CreateDatabaseAsync();
        var seeded = await SeedAsync(db, now);
        db.ChangeTracker.Clear();
        var challengeLocks = new RejectingChallengeLockProvider();
        var service = CreateService(db, new RetentionOptions(), now, challengeLocks);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunAsync(dryRun: false));

        Assert.Equal("Challenge-Fence nicht verfügbar.", exception.Message);
        Assert.Equal([seeded.DueChallengeId], challengeLocks.AcquiredChallengeIds);
        db.ChangeTracker.Clear();
        var challenge = await db.Challenges.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.DueChallengeId);
        Assert.Equal(ChallengeStatus.Open, challenge.Status);
        Assert.Null(challenge.FinishedAt);
    }

    [PostgreSqlFact]
    public async Task PostgreSqlPathUsesNativeRangesAndSkipsSqliteBackupRetention()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = $"keywars_retention_{Guid.NewGuid():N}";
        var scopedConnectionString = await CreatePostgreSqlSchemaAsync(connectionString, schema);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using var db = new PostgresKeyWarsDbContext(dbOptions);
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());

            var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
            var options = new RetentionOptions
            {
                BatchSize = 1,
                MaxBatchesPerRun = 1,
                StaleAttemptHours = 2,
                AbandonedAttemptRetentionDays = 90,
                SeenGamificationEventRetentionDays = 180,
                BackupRetentionDays = 30,
                MinimumBackupPairs = 1
            };
            var seeded = await SeedAsync(db, now);
            db.ChangeTracker.Clear();
            var service = CreateService(db, options, now);

            var dryRun = await service.RunAsync(dryRun: true);

            Assert.Equal(1, dryRun.StaleAttempts.Candidates);
            Assert.Equal(1, dryRun.ExpiredChallenges.Candidates);
            Assert.Equal(1, dryRun.AbandonedAttempts.Candidates);
            Assert.Equal(2, dryRun.SeenGamificationEvents.Candidates);
            Assert.False(dryRun.BackupPairs.Applicable);
            Assert.NotNull(dryRun.BackupPairs.SkippedReason);

            var applied = await service.RunAsync(dryRun: false);
            db.ChangeTracker.Clear();

            Assert.Equal(1, applied.StaleAttempts.Affected);
            Assert.Equal(1, applied.ExpiredChallenges.Affected);
            Assert.Equal(1, applied.AbandonedAttempts.Affected);
            Assert.Equal(1, applied.SeenGamificationEvents.Affected);
            Assert.Equal(AttemptPhase.Expired, await ReadAttemptPhaseAsync(db, seeded.StaleAttemptId));
            Assert.Equal(ChallengeStatus.Expired, await ReadChallengeStatusAsync(db, seeded.DueChallengeId));
            Assert.False(await db.TypingAttempts.AsNoTracking()
                .AnyAsync(item => item.Id == seeded.DeletableAttemptId));
            Assert.True(await db.TypingAttempts.AsNoTracking()
                .AnyAsync(item => item.Id == seeded.LedgerProtectedAttemptId));
            Assert.True(await db.TypingAttempts.AsNoTracking()
                .AnyAsync(item => item.Id == seeded.BindingProtectedAttemptId));
            Assert.Single(await db.RewardLedgerEntries.AsNoTracking().ToListAsync());
            Assert.False(applied.BackupPairs.Applicable);
        }
        finally
        {
            await DropPostgreSqlSchemaAsync(connectionString, schema);
        }
    }

    [PostgreSqlFact]
    public async Task PostgreSqlChallengeExpirationWaitsForAdvisoryFenceAndRechecksState()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = $"keywars_retention_fence_{Guid.NewGuid():N}";
        var scopedConnectionString = await CreatePostgreSqlSchemaAsync(connectionString, schema);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
            DueChallengeSeed seeded;
            await using (var setupDb = new PostgresKeyWarsDbContext(dbOptions))
            {
                await setupDb.Database.ExecuteSqlRawAsync(setupDb.Database.GenerateCreateScript());
                seeded = await SeedDueChallengeAsync(setupDb, now);
            }

            await using var blockerDb = new PostgresKeyWarsDbContext(dbOptions);
            await using var retentionDb = new PostgresKeyWarsDbContext(dbOptions);
            await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
            var advisoryKey = ChallengeAdvisoryKey(seeded.ChallengeId);
            await blockerDb.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey});");

            var challengeLocks = new SignalingChallengeLockProvider();
            var service = CreateService(retentionDb, new RetentionOptions(), now, challengeLocks);
            var retentionTask = service.RunAsync(dryRun: false);
            Assert.Equal(
                seeded.ChallengeId,
                await challengeLocks.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            await Task.Delay(200);
            Assert.False(retentionTask.IsCompleted);

            var finishedAt = now.AddMinutes(-1);
            await blockerDb.Challenges
                .Where(item => item.Id == seeded.ChallengeId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, ChallengeStatus.Finished)
                    .SetProperty(item => item.FinishedAt, finishedAt));
            await blockerTransaction.CommitAsync();

            var report = await retentionTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, report.ExpiredChallenges.Candidates);
            Assert.Equal(0, report.ExpiredChallenges.Affected);
            Assert.Equal(0, report.ExpiredChallenges.Remaining);

            var challenge = await retentionDb.Challenges.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.ChallengeId);
            Assert.Equal(ChallengeStatus.Finished, challenge.Status);
            Assert.Equal(finishedAt, challenge.FinishedAt);
            var participant = await retentionDb.ChallengeParticipants.AsNoTracking()
                .SingleAsync(item =>
                    item.ChallengeId == seeded.ChallengeId &&
                    item.UserProfileId == seeded.ParticipantId);
            Assert.Equal(ParticipantStatus.Joined, participant.Status);
            Assert.Null(participant.FinishedAt);
        }
        finally
        {
            await DropPostgreSqlSchemaAsync(connectionString, schema);
        }
    }

    [PostgreSqlFact]
    public async Task PostgreSqlAttemptExpirationWaitsForAdvisoryFenceAndRechecksState()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = $"keywars_retention_attempt_fence_{Guid.NewGuid():N}";
        var scopedConnectionString = await CreatePostgreSqlSchemaAsync(connectionString, schema);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
            SeededIds seeded;
            await using (var setupDb = new PostgresKeyWarsDbContext(dbOptions))
            {
                await setupDb.Database.ExecuteSqlRawAsync(setupDb.Database.GenerateCreateScript());
                seeded = await SeedAsync(setupDb, now);
            }

            await using var blockerDb = new PostgresKeyWarsDbContext(dbOptions);
            await using var retentionDb = new PostgresKeyWarsDbContext(dbOptions);
            await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
            var advisoryKey = AttemptAdvisoryKey(seeded.StaleAttemptId);
            await blockerDb.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey});");

            var attemptSessions = new RecordingAttemptSessionStateStore(signalAcquisition: true);
            var service = CreateService(
                retentionDb,
                new RetentionOptions(),
                now,
                attemptSessions: attemptSessions);
            var retentionTask = service.RunAsync(dryRun: false);
            Assert.Equal(
                seeded.StaleAttemptId,
                await attemptSessions.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            await Task.Delay(200);
            Assert.False(retentionTask.IsCompleted);

            var finishedAt = now.AddMinutes(-1);
            await blockerDb.TypingAttempts
                .Where(item => item.Id == seeded.StaleAttemptId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Phase, AttemptPhase.Finished)
                    .SetProperty(item => item.FinishedAt, finishedAt));
            await blockerTransaction.CommitAsync();

            var report = await retentionTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, report.StaleAttempts.Candidates);
            Assert.Equal(0, report.StaleAttempts.Affected);
            Assert.Equal(0, report.StaleAttempts.Remaining);

            var attempt = await retentionDb.TypingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == seeded.StaleAttemptId);
            Assert.Equal(AttemptPhase.Finished, attempt.Phase);
            Assert.Equal(finishedAt, attempt.FinishedAt);
        }
        finally
        {
            await DropPostgreSqlSchemaAsync(connectionString, schema);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<KeyWarsDbContext> CreateDatabaseAsync()
    {
        Directory.CreateDirectory(Path.Combine(dataDirectory, "backups"));
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DataPaths.DatabasePath(dataDirectory),
            Pooling = false
        };
        var dbOptions = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(builder.ToString())
            .Options;
        var db = new KeyWarsDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private DataRetentionService CreateService(
        KeyWarsDbContext db,
        RetentionOptions options,
        DateTimeOffset now,
        IChallengeLockProvider? challengeLocks = null,
        IAttemptSessionStateStore? attemptSessions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:DATA:DIRECTORY"] = dataDirectory
            })
            .Build();
        var environment = new TestEnvironment(dataDirectory);
        var backups = new BackupService(
            configuration,
            environment,
            NullLogger<BackupService>.Instance,
            new DatabaseRuntimeLock(configuration, environment));
        return new DataRetentionService(
            db,
            backups,
            attemptSessions ?? new AttemptSessionStore(),
            challengeLocks ?? LocalChallengeLockProvider.Shared,
            Options.Create(options),
            new ManualTimeProvider(now),
            NullLogger<DataRetentionService>.Instance);
    }

    private static async Task<string> CreatePostgreSqlSchemaAsync(string connectionString, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SearchPath = schema
        };
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA \"{schema}\";";
        await command.ExecuteNonQueryAsync();
        return builder.ConnectionString;
    }

    private static async Task DropPostgreSqlSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SeededIds> SeedAsync(KeyWarsDbContext db, DateTimeOffset now)
    {
        var profile = new UserProfile
        {
            DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
            SamAccountName = "retention-user",
            UserPrincipalName = "retention-user@example.local",
            DisplayName = "Retention User"
        };
        var text = new TrainingText
        {
            Title = "Retention-Text",
            SourceKey = "retention-text",
            Body = "Ein sicherer Testtext.",
            IsStandard = true,
            RatingEligible = true,
            Visibility = TrainingTextVisibility.Organization,
            CharacterCount = 22
        };
        db.AddRange(profile, text);

        var staleAttempt = Attempt(profile.Id, text.Id, AttemptPhase.Prepared, now.AddHours(-3));
        var deletableAttempt = Attempt(profile.Id, text.Id, AttemptPhase.Expired, now.AddDays(-120));
        var ledgerProtectedAttempt = Attempt(profile.Id, text.Id, AttemptPhase.Expired, now.AddDays(-120));
        var bindingProtectedAttempt = Attempt(profile.Id, text.Id, AttemptPhase.Prepared, now.AddMinutes(-30));
        bindingProtectedAttempt.StartedAt = bindingProtectedAttempt.PreparedAt;
        var completedAttempt = Attempt(profile.Id, text.Id, AttemptPhase.Finished, now.AddDays(-120));
        completedAttempt.Completed = true;
        completedAttempt.Official = true;
        completedAttempt.FinishedAt = now.AddDays(-120).AddMinutes(1);
        db.AddRange(staleAttempt, deletableAttempt, ledgerProtectedAttempt, bindingProtectedAttempt, completedAttempt);

        var dueChallenge = new Challenge
        {
            CreatorProfileId = profile.Id,
            TrainingTextId = text.Id,
            Title = "Überfällige Challenge",
            Status = ChallengeStatus.Open,
            CreatedAt = now.AddDays(-10),
            ExpiresAt = now.AddDays(-1)
        };
        var futureChallenge = new Challenge
        {
            CreatorProfileId = profile.Id,
            TrainingTextId = text.Id,
            Title = "Aktive Challenge",
            Status = ChallengeStatus.Running,
            CreatedAt = now,
            ExpiresAt = now.AddMilliseconds(500)
        };
        var dueRound = new ChallengeRound
        {
            ChallengeId = dueChallenge.Id,
            RoundNumber = 1,
            CreatedAt = dueChallenge.CreatedAt
        };
        db.AddRange(dueChallenge, futureChallenge, dueRound);
        db.ChallengeAttemptBindings.Add(new ChallengeAttemptBinding
        {
            ChallengeId = dueChallenge.Id,
            ChallengeRoundId = dueRound.Id,
            UserProfileId = profile.Id,
            TypingAttemptId = bindingProtectedAttempt.Id,
            TextSnapshotHash = "retention-hash",
            BindingToken = "retention-token",
            CreatedAt = now.AddDays(-120)
        });
        db.RewardLedgerEntries.Add(new RewardLedgerEntry
        {
            UserProfileId = profile.Id,
            Source = "attempt",
            SourceId = ledgerProtectedAttempt.Id.ToString("N"),
            Xp = 10,
            AwardedAt = now.AddDays(-120)
        });

        var seenEventOne = Event(profile.Id, "seen-one", now.AddDays(-220), now.AddDays(-200));
        var seenEventTwo = Event(profile.Id, "seen-two", now.AddDays(-210), now.AddDays(-190));
        var unseenEvent = Event(profile.Id, "unseen", now.AddDays(-230), null);
        db.AddRange(seenEventOne, seenEventTwo, unseenEvent);
        await db.SaveChangesAsync();

        return new SeededIds(
            staleAttempt.Id,
            deletableAttempt.Id,
            ledgerProtectedAttempt.Id,
            bindingProtectedAttempt.Id,
            completedAttempt.Id,
            dueChallenge.Id,
            unseenEvent.Id);
    }

    private static async Task<DueChallengeSeed> SeedDueChallengeAsync(
        KeyWarsDbContext db,
        DateTimeOffset now)
    {
        var profile = new UserProfile
        {
            DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
            SamAccountName = "retention-fence-user",
            UserPrincipalName = "retention-fence-user@example.local",
            DisplayName = "Retention Fence User"
        };
        var text = new TrainingText
        {
            Title = "Retention-Fence-Text",
            SourceKey = "retention-fence-text",
            Body = "Ein transaktional geschützter Testtext.",
            IsStandard = true,
            RatingEligible = true,
            Visibility = TrainingTextVisibility.Organization,
            CharacterCount = 39
        };
        var challenge = new Challenge
        {
            CreatorProfileId = profile.Id,
            TrainingTextId = text.Id,
            Title = "Gefencete Challenge",
            Status = ChallengeStatus.Running,
            CreatedAt = now.AddDays(-2),
            ExpiresAt = now.AddDays(-1)
        };
        var participant = new ChallengeParticipant
        {
            ChallengeId = challenge.Id,
            UserProfileId = profile.Id,
            Status = ParticipantStatus.Joined,
            InvitedAt = challenge.CreatedAt,
            RespondedAt = challenge.CreatedAt.AddMinutes(1)
        };
        db.AddRange(profile, text, challenge, participant);
        await db.SaveChangesAsync();
        return new DueChallengeSeed(challenge.Id, profile.Id);
    }

    private static long ChallengeAdvisoryKey(Guid challengeId)
    {
        const long challengeLockNamespace = unchecked((long)0x4348414C4C454E00);
        return BitConverter.ToInt64(challengeId.ToByteArray(), 0) ^ challengeLockNamespace;
    }

    private static long AttemptAdvisoryKey(Guid attemptId)
    {
        const long attemptLockNamespace = unchecked((long)0x415454454D505400);
        return BitConverter.ToInt64(attemptId.ToByteArray(), 0) ^ attemptLockNamespace;
    }

    private static TypingAttempt Attempt(Guid profileId, Guid textId, AttemptPhase phase, DateTimeOffset preparedAt) =>
        new()
        {
            UserProfileId = profileId,
            TrainingTextId = textId,
            Mode = TrainingMode.Text,
            Phase = phase,
            StandardTextKey = "retention-text",
            Nonce = Guid.NewGuid().ToString("N"),
            TextHash = "retention-hash",
            PreparedAt = preparedAt,
            CreatedAt = preparedAt
        };

    private static GamificationEvent Event(
        Guid profileId,
        string key,
        DateTimeOffset createdAt,
        DateTimeOffset? seenAt) =>
        new()
        {
            UserProfileId = profileId,
            Type = GamificationEventType.XpAwarded,
            EventKey = key,
            Title = key,
            Description = key,
            Source = "retention-test",
            SourceId = key,
            CreatedAt = createdAt,
            SeenAt = seenAt
        };

    private static async Task<AttemptPhase> ReadAttemptPhaseAsync(KeyWarsDbContext db, Guid id) =>
        await db.TypingAttempts.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.Phase)
            .SingleAsync();

    private static async Task<ChallengeStatus> ReadChallengeStatusAsync(KeyWarsDbContext db, Guid id) =>
        await db.Challenges.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.Status)
            .SingleAsync();

    private sealed record SeededIds(
        Guid StaleAttemptId,
        Guid DeletableAttemptId,
        Guid LedgerProtectedAttemptId,
        Guid BindingProtectedAttemptId,
        Guid CompletedAttemptId,
        Guid DueChallengeId,
        Guid UnseenEventId);

    private sealed record DueChallengeSeed(Guid ChallengeId, Guid ParticipantId);

    private sealed class RecordingAttemptSessionStateStore(
        bool rejectAcquisition = false,
        bool signalAcquisition = false) : IAttemptSessionStateStore
    {
        private readonly IAttemptSessionStateStore inner = new AttemptSessionStore();

        public List<Guid> AcquiredAttemptIds { get; } = [];
        public TaskCompletionSource<Guid> Acquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AddAsync(
            AttemptSession session,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            inner.AddAsync(session, lifetime, cancellationToken);

        public ValueTask<AttemptSession?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);

        public ValueTask<bool> TryUpdateAsync(
            AttemptSession current,
            AttemptSession updated,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            inner.TryUpdateAsync(current, updated, lifetime, cancellationToken);

        public ValueTask<AttemptSession?> RemoveAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            inner.RemoveAsync(id, cancellationToken);

        public ValueTask<IReadOnlyList<AttemptSession>> RemoveProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            inner.RemoveProfileAsync(profileId, cancellationToken);

        public async ValueTask<IOperationLease> AcquireLifecycleLockAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquiredAttemptIds.Add(id);
            if (signalAcquisition)
            {
                Acquired.TrySetResult(id);
            }

            if (rejectAcquisition)
            {
                throw new InvalidOperationException("Attempt-Fence nicht verfügbar.");
            }

            return await inner.AcquireLifecycleLockAsync(id, cancellationToken);
        }

        public ValueTask<IReadOnlyList<Guid>> GetExpiredIdsAsync(
            DateTimeOffset now,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            inner.GetExpiredIdsAsync(now, lifetime, cancellationToken);

        public ValueTask<AttemptSession?> TryRemoveExpiredAsync(
            Guid id,
            DateTimeOffset now,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default) =>
            inner.TryRemoveExpiredAsync(id, now, lifetime, cancellationToken);
    }

    private sealed class RecordingChallengeLockProvider : IChallengeLockProvider
    {
        public List<Guid> AcquiredChallengeIds { get; } = [];

        public ValueTask<IOperationLease> AcquireAsync(
            Guid challengeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquiredChallengeIds.Add(challengeId);
            return ValueTask.FromResult<IOperationLease>(new TestOperationLease());
        }
    }

    private sealed class RejectingChallengeLockProvider : IChallengeLockProvider
    {
        public List<Guid> AcquiredChallengeIds { get; } = [];

        public ValueTask<IOperationLease> AcquireAsync(
            Guid challengeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquiredChallengeIds.Add(challengeId);
            throw new InvalidOperationException("Challenge-Fence nicht verfügbar.");
        }
    }

    private sealed class SignalingChallengeLockProvider : IChallengeLockProvider
    {
        public TaskCompletionSource<Guid> Acquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IOperationLease> AcquireAsync(
            Guid challengeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquired.TrySetResult(challengeId);
            return ValueTask.FromResult<IOperationLease>(new TestOperationLease());
        }
    }

    private sealed class TestOperationLease : IOperationLease
    {
        public CancellationToken LeaseLost => CancellationToken.None;
        public void ThrowIfLost()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "KeyWars.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public const string ConnectionStringEnvironmentVariable =
        "KEYWARS_TEST_POSTGRES_CONNECTION_STRING";

    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
        {
            Skip = $"{ConnectionStringEnvironmentVariable} ist nicht gesetzt.";
        }
    }
}
