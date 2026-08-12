using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace KeyWars.IntegrationTests;

public sealed class ProfileInsightsServiceTests
{
    [Fact]
    public async Task InsightsAggregateLargeAttemptSetWithPagedHistoryAndActivity()
    {
        var now = DateTimeOffset.Parse("2026-06-19T12:00:00Z");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commandCounter = new TrendCommandCounter();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(commandCounter)
            .Options;
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
        commandCounter.Reset();
        var service = new ProfileInsightsService(db, new ManualTimeProvider(now));

        var insights = await service.GetAsync(profile, 2, 10, CancellationToken.None);

        Assert.Equal("MM", insights.Initials);
        Assert.Equal("Diamant", insights.Division);
        Assert.Equal(125, insights.Totals.CompletedAttempts);
        Assert.Equal(attempts.Sum(item => item.CorrectCharacters + item.IncorrectCharacters), insights.Totals.TypedCharacters);
        Assert.Equal(3, insights.Trends.Count);
        Assert.Equal(1, commandCounter.TrendQueryCount);
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
        foreach (var historyRow in insights.History)
        {
            var attempt = attempts.Single(item => item.Id == historyRow.Id);
            Assert.Equal(attempt.CorrectCharacters, historyRow.CorrectCharacters);
            Assert.Equal(attempt.IncorrectCharacters, historyRow.IncorrectCharacters);
            Assert.Equal(attempt.ConsistencySampleCount, historyRow.ConsistencySampleCount);
        }
        Assert.Single(insights.FeaturedAchievements);
        Assert.Single(insights.CurrentGoals);
        Assert.Single(insights.RecentEvents);
    }

    [Fact]
    public async Task ActivityReturnsContinuousNinetyDayWindowWithCorrectSumsAndBoundaries()
    {
        var now = DateTimeOffset.Parse("2026-06-19T12:00:00Z");
        var endDate = DateOnly.FromDateTime(now.UtcDateTime);
        var startDate = endDate.AddDays(-89);
        var periodStart = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var middleDate = startDate.AddDays(45);
        var middleInstant = new DateTimeOffset(middleDate.ToDateTime(new TimeOnly(8, 30)), TimeSpan.Zero);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DisplayName = "Ada Aktiv",
            SamAccountName = "aaktiv",
            DirectoryObjectGuid = Guid.NewGuid().ToString(),
            DirectorySid = "S-activity",
            CreatedAt = periodStart.AddDays(-1)
        };
        db.UserProfiles.Add(profile);

        TypingAttempt CreateAttempt(DateTimeOffset createdAt, AttemptPhase phase = AttemptPhase.Finished, bool completed = true) =>
            new()
            {
                UserProfileId = profile.Id,
                Mode = TrainingMode.Sprint60,
                Phase = phase,
                Completed = completed,
                CreatedAt = createdAt,
                PreparedAt = createdAt.AddMinutes(-1),
                StartedAt = createdAt.AddSeconds(-30),
                FinishedAt = createdAt,
                DurationMilliseconds = 30_000,
                CorrectCharacters = 100,
                TotalCharacters = 100,
                Wpm = 40,
                RawWpm = 40,
                Accuracy = 100,
                Consistency = 100,
                ConsistencySampleCount = 2
            };

        db.TypingAttempts.AddRange(
            CreateAttempt(periodStart.AddSeconds(-1)),
            CreateAttempt(periodStart),
            CreateAttempt(periodStart.AddHours(12)),
            CreateAttempt(middleInstant),
            CreateAttempt(periodEnd.AddSeconds(-1)),
            CreateAttempt(periodEnd),
            CreateAttempt(middleInstant.AddMinutes(1), AttemptPhase.Prepared),
            CreateAttempt(middleInstant.AddMinutes(2), completed: false));

        var arenaFinishes = new DateTimeOffset?[]
        {
            periodStart.AddSeconds(-1),
            periodStart,
            periodStart.AddHours(2),
            middleInstant,
            periodEnd.AddSeconds(-1),
            periodEnd,
            null
        };
        for (var index = 0; index < arenaFinishes.Length; index++)
        {
            var room = new LiveRoomSummary
            {
                Id = Guid.CreateVersion7(),
                CreatorProfileId = profile.Id,
                IdempotencyKey = $"profile-activity-{index}",
                RoomCode = $"ACT{index:000}",
                Mode = LiveRoomMode.Classic,
                Visibility = LiveRoomVisibility.InternalOpen,
                FinishedAt = arenaFinishes[index]
            };
            db.LiveRoomSummaries.Add(room);
            db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
            {
                LiveRoomSummaryId = room.Id,
                UserProfileId = profile.Id,
                Status = ParticipantStatus.Finished,
                Placement = 1,
                DurationMilliseconds = 30_000,
                Wpm = 50,
                Accuracy = 99
            });
        }

        Mission CreateMission(DateOnly date, string key, bool completed = true) =>
            new()
            {
                UserProfileId = profile.Id,
                Key = key,
                Title = key,
                Description = key,
                MissionDate = date,
                TargetValue = 1,
                CurrentValue = completed ? 1 : 0,
                Completed = completed
            };

        db.Missions.AddRange(
            CreateMission(startDate.AddDays(-1), "before"),
            CreateMission(startDate, "start-one"),
            CreateMission(startDate, "start-two"),
            CreateMission(middleDate, "incomplete", completed: false),
            CreateMission(endDate, "end"),
            CreateMission(endDate.AddDays(1), "after"));
        await db.SaveChangesAsync();
        var service = new ProfileInsightsService(db, new ManualTimeProvider(now));

        var insights = await service.GetAsync(profile, 1, 10, CancellationToken.None);

        var expectedDates = Enumerable.Range(0, 90).Select(startDate.AddDays).ToArray();
        Assert.Equal(expectedDates, insights.ActivityDays.Select(day => day.Date));
        Assert.Equal(4, insights.ActivityDays.Sum(day => day.TrainingAttempts));
        Assert.Equal(4, insights.ActivityDays.Sum(day => day.ArenaRuns));
        Assert.Equal(3, insights.ActivityDays.Sum(day => day.CompletedGoals));

        var firstDay = insights.ActivityDays[0];
        Assert.Equal((2, 2, 2), (firstDay.TrainingAttempts, firstDay.ArenaRuns, firstDay.CompletedGoals));
        Assert.Equal(0, insights.ActivityDays[1].Intensity);
        var middleDay = insights.ActivityDays.Single(day => day.Date == middleDate);
        Assert.Equal((1, 1, 0), (middleDay.TrainingAttempts, middleDay.ArenaRuns, middleDay.CompletedGoals));
        var lastDay = insights.ActivityDays[^1];
        Assert.Equal((1, 1, 1), (lastDay.TrainingAttempts, lastDay.ArenaRuns, lastDay.CompletedGoals));
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

    private sealed class TrendCommandCounter : DbCommandInterceptor
    {
        public int TrendQueryCount { get; private set; }

        public void Reset() => TrendQueryCount = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH windows", StringComparison.OrdinalIgnoreCase))
            {
                TrendQueryCount++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
