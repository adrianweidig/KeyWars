using System.Data.Common;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KeyWars.IntegrationTests;

public sealed class AchievementProgressServiceTests
{
    [Fact]
    public async Task ReadReturnsCappedProgressForEveryCatalogEntryWithFixedQueryCount()
    {
        var now = DateTimeOffset.Parse("2026-08-13T08:00:00Z");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var queryCounter = new SelectCommandCounter();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(queryCounter)
            .Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var profile = new UserProfile
        {
            DirectoryObjectGuid = Guid.NewGuid().ToString(),
            DirectorySid = "S-achievement-progress",
            SamAccountName = "aprogress",
            DisplayName = "Fortschritt Test",
            CurrentStreakDays = 40,
            RatedMatchCount = 12,
            ArenaRating = 1150
        };
        db.UserProfiles.Add(profile);

        var ownedText = new TrainingText
        {
            OwnerProfileId = profile.Id,
            Title = "Eigener Testtext",
            SourceKey = "achievement-progress",
            Body = "Ein ausreichend langer Text für den Fortschrittstest.",
            CharacterCount = 52
        };
        db.TrainingTexts.Add(ownedText);
        db.TrainingTexts.AddRange(Enumerable.Range(1, 3).Select(index => new TrainingText
        {
            OwnerProfileId = profile.Id,
            Title = $"Eigener Testtext {index}",
            SourceKey = $"achievement-progress-{index}",
            Body = "Ein weiterer ausreichend langer Text für den Fortschrittstest.",
            CharacterCount = 60
        }));
        db.TextCollections.AddRange(Enumerable.Range(1, 2).Select(index => new TextCollection
        {
            OwnerProfileId = profile.Id,
            Name = $"Sammlung {index}"
        }));

        var modes = new[]
        {
            TrainingMode.Text,
            TrainingMode.Words10,
            TrainingMode.Sprint15,
            TrainingMode.WeaknessFocus,
            TrainingMode.Precision,
            TrainingMode.Warmup,
            TrainingMode.NumbersAndSymbols
        };
        for (var index = 0; index < modes.Length; index++)
        {
            db.TypingAttempts.Add(new TypingAttempt
            {
                UserProfileId = profile.Id,
                Mode = modes[index],
                Phase = AttemptPhase.Finished,
                PreparedAt = now.AddMinutes(-index - 1),
                StartedAt = now.AddMinutes(-index).AddSeconds(-30),
                FinishedAt = now.AddMinutes(-index),
                DurationMilliseconds = 30_000,
                CorrectCharacters = 100,
                TotalCharacters = 100,
                Wpm = 120 - index * 10,
                Accuracy = 100 - index,
                Completed = true,
                Official = true,
                CompetitionIntegrityEligible = true
            });
        }

        var room = new LiveRoomSummary
        {
            Id = Guid.CreateVersion7(),
            IdempotencyKey = "achievement-progress-room",
            CreatorProfileId = profile.Id,
            RoomCode = "APROG1",
            TargetTextHash = "sha256:achievement-progress",
            Mode = LiveRoomMode.Classic,
            Visibility = LiveRoomVisibility.InvitationOnly,
            RoundCount = 1,
            CreatedAt = now.AddHours(-1),
            StartedAt = now.AddMinutes(-2),
            FinishedAt = now.AddMinutes(-1)
        };
        db.LiveRoomSummaries.Add(room);
        db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
        {
            LiveRoomSummaryId = room.Id,
            UserProfileId = profile.Id,
            Status = ParticipantStatus.Finished,
            CompetitionEligible = true,
            DurationMilliseconds = 30_000,
            Wpm = 125,
            Accuracy = 100
        });

        for (var index = 1; index <= 3; index++)
        {
            var challenge = new Challenge
            {
                CreatorProfileId = profile.Id,
                TrainingTextId = ownedText.Id,
                Title = $"Fortschritt {index}",
                Status = ChallengeStatus.Finished,
                FinishedAt = now.AddDays(-index)
            };
            var round = new ChallengeRound
            {
                ChallengeId = challenge.Id,
                RoundNumber = 1
            };
            db.Challenges.Add(challenge);
            db.ChallengeRounds.Add(round);
            db.ChallengeRoundResults.Add(new ChallengeRoundResult
            {
                ChallengeRoundId = round.Id,
                UserProfileId = profile.Id,
                Status = ParticipantStatus.Finished,
                CompetitionEligible = true,
                DurationMilliseconds = 30_000,
                Wpm = 60,
                Accuracy = 96 + index,
                FinishedAt = now.AddDays(-index)
            });
        }

        db.Missions.AddRange(Enumerable.Range(1, 6).Select(index => new Mission
        {
            UserProfileId = profile.Id,
            Key = index == 6 ? MissionKeys.WeeklyRounds : $"completed-{index}",
            Title = $"Mission {index}",
            Description = "Abgeschlossene Testmission",
            MissionDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-index),
            TargetValue = 1,
            CurrentValue = 1,
            Completed = true
        }));
        db.Achievements.Add(new Achievement
        {
            UserProfileId = profile.Id,
            Key = "erster-versuch",
            Title = "Erster gültiger Versuch",
            Description = "Bereits freigeschaltet",
            UnlockedAt = now
        });
        await db.SaveChangesAsync();

        queryCounter.Reset();
        var overview = await new AchievementProgressService(db).ReadAsync(profile.Id);

        Assert.Equal(6, queryCounter.SelectCount);
        Assert.Equal(1, overview.UnlockedCount);
        Assert.Equal(
            MotivationCatalog.AchievementDefinitions.Select(item => item.Key).OrderBy(item => item),
            overview.Items.Keys.OrderBy(item => item));
        Assert.All(overview.Items.Values, item => Assert.InRange(item.Current, 0, item.Target));
        Assert.Equal(now, overview.Items["erster-versuch"].UnlockedAt);
        AssertProgress(overview, "training-5-attempts", 5, 5, "Runden");
        AssertProgress(overview, "training-10-attempts", 7, 10, "Runden");
        AssertProgress(overview, "precision-100", 99.9, 99.9, "%");
        AssertProgress(overview, "speed-100", 100, 100, "WPM");
        AssertProgress(overview, "speed-personal-best", 120, 122, "WPM");
        AssertProgress(overview, "streak-30", 30, 30, "Tage");
        AssertProgress(overview, "arena-10", 10, 10, "Rennen");
        AssertProgress(overview, "arena-rating-1100", 1100, 1100, "Rating-Punkte");
        AssertProgress(overview, "text-author-3", 3, 3, "Texte");
        AssertProgress(overview, "team-3-challenges", 3, 3, "Herausforderungen");
        AssertProgress(overview, "mission-5", 5, 5, "Missionen");
        AssertProgress(overview, "mission-weekly", 1, 1, "Wochenmission");
    }

    [Fact]
    public async Task ReadExcludesIntegrityIneligibleAttemptsButKeepsEligibleTrainingModes()
    {
        var now = DateTimeOffset.Parse("2026-08-13T09:00:00Z");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var profile = new UserProfile
        {
            DirectoryObjectGuid = Guid.NewGuid().ToString(),
            DirectorySid = "S-achievement-integrity",
            SamAccountName = "aintegrity",
            DisplayName = "Integrität Test"
        };
        db.UserProfiles.Add(profile);
        db.TypingAttempts.AddRange(
            CreateAttempt(TrainingMode.WeaknessFocus, 42, 96, competitionIntegrityEligible: true),
            CreateAttempt(TrainingMode.Words100, 500, 100, competitionIntegrityEligible: false));
        await db.SaveChangesAsync();
        Assert.Single(await db.TypingAttempts.Where(item => item.CompetitionIntegrityEligible).ToListAsync());

        var overview = await new AchievementProgressService(db).ReadAsync(profile.Id);

        AssertProgress(overview, "training-10-attempts", 1, 10, "Runden");
        AssertProgress(overview, "training-weakness-focus", 1, 1, "Runde");
        AssertProgress(overview, "training-words-round", 0, 1, "Runde");
        AssertProgress(overview, "precision-100", 96, 99.9, "%");
        AssertProgress(overview, "speed-60", 42, 60, "WPM");
        AssertProgress(overview, "speed-personal-best", 42, 44, "WPM");

        TypingAttempt CreateAttempt(
            TrainingMode mode,
            double wpm,
            double accuracy,
            bool competitionIntegrityEligible) => new()
        {
            UserProfileId = profile.Id,
            Mode = mode,
            Phase = AttemptPhase.Finished,
            PreparedAt = now.AddMinutes(-1),
            StartedAt = now.AddSeconds(-30),
            FinishedAt = now,
            DurationMilliseconds = 30_000,
            CorrectCharacters = 100,
            TotalCharacters = 100,
            Wpm = wpm,
            Accuracy = accuracy,
            Completed = true,
            Official = true,
            CompetitionIntegrityEligible = competitionIntegrityEligible
        };
    }

    private static void AssertProgress(
        AchievementProgressOverview overview,
        string key,
        double current,
        double target,
        string unit)
    {
        var progress = overview.Items[key];
        Assert.Equal(current, progress.Current, 5);
        Assert.Equal(target, progress.Target, 5);
        Assert.Equal(unit, progress.Unit);
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        public int SelectCount { get; private set; }

        public void Reset() => SelectCount = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCount++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
