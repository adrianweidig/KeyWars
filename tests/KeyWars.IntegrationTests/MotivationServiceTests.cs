using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.IntegrationTests;

public sealed class MotivationServiceTests
{
    [Fact]
    public async Task CompletedMissionAwardsXpOnlyOnceAndDoesNotDependOnTitle()
    {
        await using var context = await MotivationTestContext.CreateAsync();
        var service = context.Service;
        var today = DateOnly.FromDateTime(context.Time.GetUtcNow().Date);
        await service.EnsureCurrentMissionsAsync(context.Profile.Id, today);
        var accuracyMission = await context.Db.Missions.SingleAsync(item => item.UserProfileId == context.Profile.Id && item.Key == "daily-accuracy");
        accuracyMission.Title = "Umbenannt";
        await context.Db.SaveChangesAsync();
        var attempt = context.CreateAttempt(accuracy: 98, wpm: 50, totalCharacters: 300);
        context.Db.TypingAttempts.Add(attempt);
        await context.Db.SaveChangesAsync();

        await service.ApplyAttemptAsync(context.Profile.Id, attempt, "Sicher tippen", CancellationToken.None);
        await context.Db.SaveChangesAsync();
        var firstXp = context.Profile.ExperiencePoints;
        attempt.ExperienceAwarded = false;
        await service.ApplyAttemptAsync(context.Profile.Id, attempt, "Sicher tippen", CancellationToken.None);
        await context.Db.SaveChangesAsync();

        var ledger = await context.Db.RewardLedgerEntries.OrderBy(item => item.Source).ToListAsync();
        var storedMission = await context.Db.Missions.SingleAsync(item => item.Id == accuracyMission.Id);
        Assert.Equal(105, firstXp);
        Assert.Equal(firstXp, context.Profile.ExperiencePoints);
        Assert.True(attempt.ExperienceAwarded);
        Assert.True(storedMission.Completed);
        Assert.Equal("Umbenannt", storedMission.Title);
        Assert.Contains(ledger, item => item.Source == "attempt" && item.SourceId == attempt.Id.ToString("N") && item.Xp == 70);
        Assert.Contains(ledger, item => item.Source == "mission" && item.SourceId == accuracyMission.Id.ToString("N") && item.Xp == 35);
        Assert.Equal(2, ledger.Count);

        var events = await context.Db.GamificationEvents.ToListAsync();
        Assert.Contains(events, item => item.Type == GamificationEventType.XpAwarded && item.Source == "attempt" && item.SourceId == attempt.Id.ToString("N") && item.XpDelta == 70);
        Assert.Contains(events, item => item.Type == GamificationEventType.MissionCompleted && item.Source == "mission" && item.SourceId == accuracyMission.Id.ToString("N") && item.XpDelta == 35);
        Assert.Equal(
            events.Count,
            events.Select(item => (item.UserProfileId, item.Source, item.SourceId, item.EventKey)).Distinct().Count());
    }

    [Fact]
    public async Task DailyAndWeeklyMissionsAreDeterministicForCurrentPeriods()
    {
        await using var context = await MotivationTestContext.CreateAsync("2026-06-17T12:00:00Z");
        var today = DateOnly.FromDateTime(context.Time.GetUtcNow().Date);
        var weekStart = MotivationService.GetWeekStart(today);

        await context.Service.EnsureCurrentMissionsAsync(context.Profile.Id, today);
        await context.Service.EnsureCurrentMissionsAsync(context.Profile.Id, today);
        var missions = await context.Db.Missions.OrderBy(item => item.Key).ToListAsync();

        Assert.Equal(8, missions.Count);
        Assert.Equal(4, missions.Count(item => item.MissionDate == today && item.Key.StartsWith("daily-")));
        Assert.Equal(4, missions.Count(item => item.MissionDate == weekStart && item.Key.StartsWith("weekly-")));
        Assert.Equal(missions.Count, missions.Select(item => (item.MissionDate, item.Key)).Distinct().Count());
    }

    [Fact]
    public void MotivationCatalogDefinesUniqueMissionContracts()
    {
        var definitions = MotivationCatalog.MissionDefinitions;

        Assert.Equal(8, definitions.Count);
        Assert.Equal(definitions.Count, definitions.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, definitions.Count(item => item.Cadence == MissionCadence.Daily));
        Assert.Equal(4, definitions.Count(item => item.Cadence == MissionCadence.Weekly));
        Assert.Contains(definitions, item => item.Key == MissionKeys.DailyThreeRounds);
        Assert.Contains(definitions, item => item.Key == MissionKeys.WeeklyTexts);
        Assert.All(definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Title));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.True(definition.TargetValue > 0);
            Assert.True(definition.XpReward > 0);
        });
    }

    [Fact]
    public async Task UltraShortAttemptsDoNotAwardXpOrProgressMissions()
    {
        await using var context = await MotivationTestContext.CreateAsync();
        var attempt = context.CreateAttempt(accuracy: 100, wpm: 120, totalCharacters: 10, durationMilliseconds: 1_000);
        context.Db.TypingAttempts.Add(attempt);
        await context.Db.SaveChangesAsync();

        await context.Service.ApplyAttemptAsync(context.Profile.Id, attempt, "kurz", CancellationToken.None);
        await context.Db.SaveChangesAsync();

        Assert.Equal(0, context.Profile.ExperiencePoints);
        Assert.True(attempt.ExperienceAwarded);
        Assert.Empty(await context.Db.RewardLedgerEntries.ToListAsync());
        Assert.Empty(await context.Db.Missions.ToListAsync());
        Assert.Empty(await context.Db.GamificationEvents.ToListAsync());
    }

    [Fact]
    public async Task ArenaResultsUseRewardLedgerAndMissionProgressOnce()
    {
        await using var context = await MotivationTestContext.CreateAsync();

        await context.Service.ApplyArenaResultAsync(context.Profile.Id, "arena-room-1:anna", 72, 99, 30_000);
        await context.Db.SaveChangesAsync();
        var firstXp = context.Profile.ExperiencePoints;
        await context.Service.ApplyArenaResultAsync(context.Profile.Id, "arena-room-1:anna", 72, 99, 30_000);
        await context.Db.SaveChangesAsync();

        var ledger = await context.Db.RewardLedgerEntries.ToListAsync();
        var arenaMission = await context.Db.Missions.SingleAsync(item => item.Key == "daily-arena-or-team");
        var achievements = await context.Db.Achievements.Select(item => item.Key).ToListAsync();
        Assert.True(firstXp > 0);
        Assert.Equal(firstXp, context.Profile.ExperiencePoints);
        Assert.Single(ledger, item => item.Source == "arena" && item.SourceId == "arena-room-1:anna");
        Assert.True(arenaMission.Completed);
        Assert.Contains("arena-first", achievements);

        var events = await context.Db.GamificationEvents.ToListAsync();
        Assert.Single(events, item => item.Type == GamificationEventType.XpAwarded && item.Source == "arena" && item.SourceId == "arena-room-1:anna");
        Assert.Single(events, item => item.Type == GamificationEventType.ArenaResult && item.Source == "arena" && item.SourceId == "arena-room-1:anna");
        Assert.Equal(
            events.Count,
            events.Select(item => (item.UserProfileId, item.Source, item.SourceId, item.EventKey)).Distinct().Count());
    }

    [Fact]
    public async Task LevelUpWritesBeforeAfterEventAndOutcome()
    {
        await using var context = await MotivationTestContext.CreateAsync();
        context.Profile.ExperiencePoints = 190;
        context.Profile.Level = 1;
        await context.Db.SaveChangesAsync();
        var attempt = context.CreateAttempt(accuracy: 98, wpm: 50, totalCharacters: 300);
        context.Db.TypingAttempts.Add(attempt);
        await context.Db.SaveChangesAsync();

        var outcome = await context.Service.ApplyAttemptAsync(context.Profile.Id, attempt, "Sicher tippen", CancellationToken.None);
        await context.Db.SaveChangesAsync();

        var levelEvent = await context.Db.GamificationEvents.SingleAsync(item => item.Type == GamificationEventType.LevelUp);
        Assert.Equal(1, levelEvent.LevelBefore);
        Assert.Equal(2, levelEvent.LevelAfter);
        Assert.Equal(GamificationRarity.Rare, levelEvent.Rarity);
        Assert.Equal(1, outcome.LevelBefore);
        Assert.Equal(2, outcome.LevelAfter);
        Assert.True(outcome.XpDelta > 0);
    }

    [Fact]
    public async Task GamificationEventWriterNormalizesFieldsAndDeduplicatesBeforeSave()
    {
        await using var context = await MotivationTestContext.CreateAsync();
        var writer = new GamificationEventWriter(context.Db);
        var createdEvents = new List<GamificationEvent>();
        var draft = new GamificationEventDraft(
            GamificationEventType.PersonalBest,
            $"  {new string('k', 120)}  ",
            $"  {new string('t', 200)}  ",
            $"  {new string('d', 400)}  ",
            0,
            2,
            3,
            GamificationRarity.Rare,
            $"  {new string('s', 100)}  ",
            $"  {new string('i', 120)}  ");

        await writer.AddAsync(createdEvents, context.Profile, draft, context.Time.GetUtcNow(), CancellationToken.None);
        await writer.AddAsync(createdEvents, context.Profile, draft, context.Time.GetUtcNow(), CancellationToken.None);
        await context.Db.SaveChangesAsync();

        var stored = await context.Db.GamificationEvents.SingleAsync();
        Assert.Single(createdEvents);
        Assert.Equal(new string('s', 64), stored.Source);
        Assert.Equal(new string('i', 80), stored.SourceId);
        Assert.Equal(new string('k', 80), stored.EventKey);
        Assert.Equal(new string('t', 160), stored.Title);
        Assert.Equal(new string('d', 360), stored.Description);
        Assert.Equal(2, stored.LevelBefore);
        Assert.Equal(3, stored.LevelAfter);
    }

    [Fact]
    public void AchievementDefinitionsAreStableAndCoverRequiredCategories()
    {
        var definitions = MotivationService.AchievementDefinitions;

        Assert.True(definitions.Count >= 30);
        Assert.Equal(definitions.Count, definitions.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Key));
            Assert.False(string.IsNullOrWhiteSpace(definition.Title));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
        });
        Assert.Contains(definitions, item => item.Category == "Training");
        Assert.Contains(definitions, item => item.Category == "Präzision");
        Assert.Contains(definitions, item => item.Category == "Tempo");
        Assert.Contains(definitions, item => item.Category == "Serie");
        Assert.Contains(definitions, item => item.Category == "Arena");
        Assert.Contains(definitions, item => item.Category == "Texte");
        Assert.Contains(definitions, item => item.Category == "Team");
    }

    private sealed class MotivationTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private MotivationTestContext(SqliteConnection connection, KeyWarsDbContext db, ManualTimeProvider time, UserProfile profile)
        {
            this.connection = connection;
            Db = db;
            Time = time;
            Profile = profile;
            Service = new MotivationService(db, time);
        }

        public KeyWarsDbContext Db { get; }
        public ManualTimeProvider Time { get; }
        public UserProfile Profile { get; }
        public MotivationService Service { get; }

        public static async Task<MotivationTestContext> CreateAsync(string utcNow = "2026-06-18T12:02:00Z")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var profile = new UserProfile
            {
                DisplayName = "Mona Mission",
                SamAccountName = "mmission",
                DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
                DirectorySid = "S-1-5-21-mission"
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
            return new MotivationTestContext(connection, db, new ManualTimeProvider(DateTimeOffset.Parse(utcNow)), profile);
        }

        public TypingAttempt CreateAttempt(double accuracy, double wpm, int totalCharacters, int durationMilliseconds = 60_000) => new()
        {
            UserProfileId = Profile.Id,
            Mode = TrainingMode.Sprint60,
            Phase = AttemptPhase.Finished,
            PreparedAt = Time.GetUtcNow().AddMinutes(-1),
            StartedAt = Time.GetUtcNow().AddMinutes(-1),
            FinishedAt = Time.GetUtcNow(),
            DurationMilliseconds = durationMilliseconds,
            CorrectCharacters = totalCharacters,
            TotalCharacters = totalCharacters,
            Wpm = wpm,
            Accuracy = accuracy,
            Consistency = 96,
            Completed = true,
            Official = true
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
