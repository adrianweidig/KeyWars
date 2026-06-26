using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.IntegrationTests;

public sealed class ProfileInsightsServiceTests
{
    [Fact]
    public async Task InsightsAggregateLargeAttemptSetWithPagedHistoryAndActivity()
    {
        var now = DateTimeOffset.Parse("2026-06-19T12:00:00Z");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DisplayName = "Mara Muster",
            SamAccountName = "mmuster",
            DirectoryObjectGuid = Guid.NewGuid().ToString(),
            DirectorySid = "S-33",
            ArenaRating = 1420,
            CreatedAt = now.AddMonths(-3)
        };
        db.UserProfiles.Add(profile);
        var attempts = Enumerable.Range(0, 125)
            .Select(index => new TypingAttempt
            {
                UserProfileId = profile.Id,
                Mode = index % 2 == 0 ? TrainingMode.Sprint60 : TrainingMode.Words25,
                Phase = AttemptPhase.Finished,
                Completed = true,
                Official = true,
                CreatedAt = now.AddDays(-(index % 100)).AddMinutes(-index - 1),
                PreparedAt = now.AddDays(-(index % 100)).AddMinutes(-index - 2),
                StartedAt = now.AddDays(-(index % 100)).AddMinutes(-index - 2),
                FinishedAt = now.AddDays(-(index % 100)).AddMinutes(-index - 1),
                DurationMilliseconds = 30_000 + index,
                CorrectCharacters = 100 + index,
                IncorrectCharacters = index % 4,
                TotalCharacters = 120 + index,
                Wpm = 42 + index % 30,
                RawWpm = 43 + index % 30,
                Accuracy = 94 + index % 5,
                Consistency = 78 + index % 12,
                ConsistencySampleCount = 5
            })
            .ToList();
        db.TypingAttempts.AddRange(attempts);
        var room = new LiveRoomSummary
        {
            Id = Guid.CreateVersion7(),
            CreatorProfileId = profile.Id,
            IdempotencyKey = "room-activity",
            RoomCode = "ABC123",
            Mode = LiveRoomMode.Classic,
            Visibility = LiveRoomVisibility.InternalOpen,
            FinishedAt = now.AddHours(-2)
        };
        db.LiveRoomSummaries.Add(room);
        db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
        {
            LiveRoomSummaryId = room.Id,
            UserProfileId = profile.Id,
            Status = ParticipantStatus.Finished,
            Placement = 1,
            Wpm = 70,
            Accuracy = 99,
            DurationMilliseconds = 28_000
        });
        db.Missions.Add(new Mission
        {
            UserProfileId = profile.Id,
            Key = "daily-volume",
            Title = "Trainingsvolumen",
            Description = "Schließe Training ab.",
            MissionDate = DateOnly.FromDateTime(now.UtcDateTime),
            TargetValue = 2,
            CurrentValue = 2,
            Completed = true,
            XpReward = 30
        });
        db.Achievements.Add(new Achievement
        {
            UserProfileId = profile.Id,
            Key = "first-pace",
            Title = "Tempo gefunden",
            Description = "Erster schneller Versuch.",
            UnlockedAt = now.AddHours(-1)
        });
        db.GamificationEvents.Add(new GamificationEvent
        {
            UserProfileId = profile.Id,
            Type = GamificationEventType.AchievementUnlocked,
            EventKey = "achievement-unlocked",
            Title = "Tempo gefunden",
            Description = "Erster schneller Versuch.",
            LevelBefore = 1,
            LevelAfter = 1,
            Rarity = GamificationRarity.Common,
            Source = "achievement",
            SourceId = "first-pace",
            CreatedAt = now.AddHours(-1)
        });
        await db.SaveChangesAsync();
        var service = new ProfileInsightsService(db, new ManualTimeProvider(now));

        var insights = await service.GetAsync(profile, 2, 10, CancellationToken.None);

        Assert.Equal("MM", insights.Initials);
        Assert.Equal("Gold", insights.Division);
        Assert.Equal(125, insights.Totals.CompletedAttempts);
        Assert.Equal(attempts.Sum(item => item.CorrectCharacters + item.IncorrectCharacters), insights.Totals.TypedCharacters);
        Assert.Equal(3, insights.Trends.Count);
        Assert.True(insights.Trends.Single(item => item.Days == 7).SampleCount > 0);
        Assert.Equal(90, insights.ActivityDays.Count);
        var today = insights.ActivityDays[^1];
        Assert.True(today.TrainingAttempts > 0);
        Assert.Equal(1, today.ArenaRuns);
        Assert.Equal(1, today.CompletedGoals);
        Assert.Equal(2, insights.BestModes.Count);
        Assert.Equal(2, insights.HistoryPage);
        Assert.Equal(10, insights.History.Count);
        Assert.Equal(125, insights.HistoryTotalItems);
        Assert.Equal(13, insights.HistoryTotalPages);
        Assert.Single(insights.FeaturedAchievements);
        Assert.Single(insights.CurrentGoals);
        Assert.Single(insights.RecentEvents);
    }

    [Fact]
    public async Task InsightsReturnStableEmptyStateForNewProfile()
    {
        var now = DateTimeOffset.Parse("2026-06-19T12:00:00Z");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DisplayName = "Lea Test",
            SamAccountName = "ltest",
            DirectoryObjectGuid = Guid.NewGuid().ToString(),
            DirectorySid = "S-34"
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();
        var service = new ProfileInsightsService(db, new ManualTimeProvider(now));

        var insights = await service.GetAsync(profile, 5, 10, CancellationToken.None);

        Assert.Equal("LT", insights.Initials);
        Assert.Equal("Bronze", insights.Division);
        Assert.Equal(0, insights.Totals.CompletedAttempts);
        Assert.All(insights.Trends, trend => Assert.Equal(0, trend.SampleCount));
        Assert.Equal(90, insights.ActivityDays.Count);
        Assert.Empty(insights.History);
        Assert.Equal(1, insights.HistoryPage);
        Assert.Equal(1, insights.HistoryTotalPages);
        Assert.Empty(insights.RecentEvents);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
