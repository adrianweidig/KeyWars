using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace KeyWars.IntegrationTests;

public sealed class CompetitionLeaderboardServiceTests
{
    [Fact]
    public async Task SprintBoardUsesBestVisibleEligibleAttemptPerProfile()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var bob = context.AddProfile("bob", "Bob Beta");
        var hidden = context.AddProfile("hidden", "Hidden Hero", visible: false);
        context.AddAttempt(alice.Id, TrainingMode.Sprint60, 72, 97, context.Now.AddMinutes(-20));
        context.AddAttempt(alice.Id, TrainingMode.Sprint60, 82, 96, context.Now.AddMinutes(-10));
        context.AddAttempt(alice.Id, TrainingMode.Sprint60, 140, 99, context.Now.AddDays(-2));
        context.AddAttempt(bob.Id, TrainingMode.Sprint60, 90, 89, context.Now.AddMinutes(-9));
        context.AddAttempt(bob.Id, TrainingMode.Sprint60, 79, 98, context.Now.AddMinutes(-8));
        context.AddAttempt(hidden.Id, TrainingMode.Sprint60, 120, 99, context.Now.AddMinutes(-7));
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(CompetitionBoardKind.Sprint, CompetitionPeriod.Day, TrainingMode.Sprint60, null));

        Assert.Equal([alice.Id, bob.Id], result.Board.Entries.Select(entry => entry.UserProfileId).ToArray());
        Assert.Equal(82, result.Board.Entries[0].Wpm);
        Assert.Equal(1, result.Board.OwnEntry?.Rank);
        Assert.DoesNotContain(result.Board.Entries, entry => entry.UserProfileId == hidden.Id);
    }

    [Fact]
    public async Task AttemptBoardAndPersonalBestExcludeIntegrityIneligibleLegacyAttempts()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var legacyOnly = context.AddProfile("legacy", "Legacy Result");
        context.AddAttempt(
            alice.Id,
            TrainingMode.Sprint60,
            180,
            99,
            context.Now.AddMinutes(-2),
            competitionIntegrityEligible: false);
        context.AddAttempt(alice.Id, TrainingMode.Sprint60, 80, 98, context.Now.AddMinutes(-1));
        context.AddAttempt(
            legacyOnly.Id,
            TrainingMode.Sprint60,
            190,
            99,
            context.Now.AddMinutes(-1),
            competitionIntegrityEligible: false);
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(
            alice,
            new LeaderboardQuery(
                CompetitionBoardKind.Sprint,
                CompetitionPeriod.Day,
                TrainingMode.Sprint60,
                null));

        var entry = Assert.Single(result.Board.Entries);
        Assert.Equal(alice.Id, entry.UserProfileId);
        Assert.Equal(80, entry.Wpm);
        Assert.Equal("80.0 WPM", result.PersonalBest);
        Assert.DoesNotContain(result.Board.Entries, item => item.UserProfileId == legacyOnly.Id);
    }

    [Fact]
    public async Task SprintBoardLimitsPublicRowsAfterSelectingEachProfilesBestAttempt()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var profiles = Enumerable.Range(0, 105)
            .Select(index => context.AddProfile($"user-{index:000}", $"Person {index:000}"))
            .ToList();
        foreach (var (profile, index) in profiles.Select((profile, index) => (profile, index)))
        {
            context.AddAttempt(profile.Id, TrainingMode.Sprint60, 50 + index, 99, context.Now.AddMinutes(-index - 1));
            context.AddAttempt(profile.Id, TrainingMode.Sprint60, 40 + index, 99, context.Now.AddMinutes(-index - 2));
        }

        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(profiles[0], new LeaderboardQuery(CompetitionBoardKind.Sprint, CompetitionPeriod.Day, TrainingMode.Sprint60, null));

        Assert.Equal(100, result.Board.Entries.Count);
        Assert.Equal(profiles[^1].Id, result.Board.Entries[0].UserProfileId);
        Assert.Equal(154, result.Board.Entries[0].Wpm);
    }

    [Fact]
    public async Task DashboardBoardUsesOneQueryWithoutOverviewMetadata()
    {
        var queryProbe = new LeaderboardQueryProbe();
        await using var context = await CompetitionTestContext.CreateAsync(queryProbe);
        var alice = context.AddProfile("alice", "Alice Alpha");
        context.AddText("Nicht für den Dashboardpfad laden", ratingEligible: true);
        context.AddAttempt(alice.Id, TrainingMode.Sprint60, 82, 99, context.Now.AddMinutes(-1));
        await context.Db.SaveChangesAsync();
        queryProbe.Reset();

        var board = await context.Service.GetBoardAsync(
            alice,
            new LeaderboardQuery(
                CompetitionBoardKind.Sprint,
                CompetitionPeriod.Day,
                TrainingMode.Sprint60,
                null));

        Assert.Single(board.Entries);
        var command = Assert.Single(queryProbe.ReaderCommands);
        Assert.Contains("TypingAttempts", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CharacterCount", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TrainingTexts", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FinishedAt", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("substr(COALESCE", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROW_NUMBER() OVER", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DepartmentScopeFiltersVisibleProfilesOnTheServer()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha", department: "Entwicklung");
        var bob = context.AddProfile("bob", "Bob Beta", department: "Entwicklung");
        var outside = context.AddProfile("outside", "Olivia Ops", department: "Betrieb");
        context.AddAttempt(alice.Id, TrainingMode.Sprint60, 70, 99, context.Now.AddMinutes(-3));
        context.AddAttempt(bob.Id, TrainingMode.Sprint60, 80, 99, context.Now.AddMinutes(-2));
        context.AddAttempt(outside.Id, TrainingMode.Sprint60, 120, 99, context.Now.AddMinutes(-1));
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(
            CompetitionBoardKind.Sprint,
            CompetitionPeriod.Day,
            TrainingMode.Sprint60,
            null,
            OwnDepartmentOnly: true));

        Assert.True(result.Query.OwnDepartmentOnly);
        Assert.Equal([bob.Id, alice.Id], result.Board.Entries.Select(entry => entry.UserProfileId).ToArray());
        Assert.DoesNotContain(result.Board.Entries, entry => entry.UserProfileId == outside.Id);
    }

    [Fact]
    public async Task TextBoardRequiresRatingEligibleText()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var ratingText = context.AddText("Wertung", ratingEligible: true);
        var privateText = context.AddText("Privat", ratingEligible: false);
        context.AddAttempt(alice.Id, TrainingMode.Text, 70, 99, context.Now.AddMinutes(-5), ratingText.Id);
        context.AddAttempt(alice.Id, TrainingMode.Text, 120, 99, context.Now.AddMinutes(-4), privateText.Id, leaderboardEligible: true);
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(CompetitionBoardKind.Text, CompetitionPeriod.Day, TrainingMode.Sprint60, ratingText.Id));

        var entry = Assert.Single(result.Board.Entries);
        Assert.Equal(70, entry.Wpm);
        Assert.Equal(ratingText.Id, entry.TrainingTextId);
    }

    [Fact]
    public async Task TextBoardUsesOnlyAttemptsForTheCurrentTextBody()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var oldOnly = context.AddProfile("old-only", "Historische Person");
        var text = context.AddText("Veränderbarer Text", ratingEligible: true);
        var oldHash = TextHash.Compute(text.Body);
        context.AddAttempt(alice.Id, TrainingMode.Text, 120, 99, context.Now.AddMinutes(-5), text.Id, textHash: oldHash);
        context.AddAttempt(oldOnly.Id, TrainingMode.Text, 130, 99, context.Now.AddMinutes(-4), text.Id, textHash: oldHash);

        text.Body = "Die aktuelle Textfassung ersetzt den früheren Inhalt vollständig.";
        text.CharacterCount = text.Body.Length;
        text.UpdatedAt = context.Now;
        context.AddAttempt(
            alice.Id,
            TrainingMode.Text,
            70,
            98,
            context.Now.AddMinutes(-1),
            text.Id,
            textHash: TextHash.Compute(text.Body));
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(
            alice,
            new LeaderboardQuery(CompetitionBoardKind.Text, CompetitionPeriod.Day, TrainingMode.Sprint60, text.Id));

        var entry = Assert.Single(result.Board.Entries);
        Assert.Equal(alice.Id, entry.UserProfileId);
        Assert.Equal(70, entry.Wpm);
        Assert.DoesNotContain(result.Board.Entries, item => item.UserProfileId == oldOnly.Id);
    }

    [Fact]
    public async Task ArenaBoardExcludesAbortedRoomsAndHiddenProfiles()
    {
        var queryProbe = new LeaderboardQueryProbe();
        await using var context = await CompetitionTestContext.CreateAsync(queryProbe);
        var alice = context.AddProfile("alice", "Alice Alpha", arenaRating: 1100);
        var bob = context.AddProfile("bob", "Bob Beta", arenaRating: 1050);
        var hidden = context.AddProfile("hidden", "Hidden Hero", visible: false, arenaRating: 1400);
        context.AddArenaRoom(alice.Id, bob.Id, aborted: false);
        context.AddArenaRoom(hidden.Id, alice.Id, aborted: false);
        context.AddArenaRoom(bob.Id, alice.Id, aborted: true);
        context.AddArenaRoom(alice.Id, bob.Id, aborted: false, competitionEligible: false);
        await context.Db.SaveChangesAsync();
        queryProbe.Reset();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(CompetitionBoardKind.ArenaRating, CompetitionPeriod.Day, TrainingMode.Sprint60, null));

        Assert.Equal([alice.Id, bob.Id], result.Board.Entries.Select(entry => entry.UserProfileId).ToArray());
        Assert.Equal(2, result.Board.Entries.Single(entry => entry.UserProfileId == alice.Id).Attempts);
        Assert.Equal(1, result.Board.Entries.Single(entry => entry.UserProfileId == bob.Id).Attempts);
        Assert.DoesNotContain(result.Board.Entries, entry => entry.UserProfileId == hidden.Id);
        var arenaStatsCommand = Assert.Single(queryProbe.ReaderCommands, command =>
            command.Contains("LiveRoomSummaries", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("LiveRoomParticipantSummaries", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("substr(FinishedAt", arenaStatsCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChallengeBoardUsesFinishedRoundResults()
    {
        var queryProbe = new LeaderboardQueryProbe();
        await using var context = await CompetitionTestContext.CreateAsync(queryProbe);
        var alice = context.AddProfile("alice", "Alice Alpha");
        var bob = context.AddProfile("bob", "Bob Beta");
        var text = context.AddText("Challenge-Text", ratingEligible: true);
        var challenge = context.AddChallenge(text.Id, alice.Id, "Teamrennen");
        context.AddChallengeResult(challenge.Id, alice.Id, 75, 99, ParticipantStatus.Finished);
        context.AddChallengeResult(
            challenge.Id,
            bob.Id,
            110,
            99,
            ParticipantStatus.Finished,
            competitionEligible: false);
        await context.Db.SaveChangesAsync();
        queryProbe.Reset();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(CompetitionBoardKind.Challenge, CompetitionPeriod.Day, TrainingMode.Sprint60, null));

        var entry = Assert.Single(result.Board.Entries);
        Assert.Equal(alice.Id, entry.UserProfileId);
        Assert.Equal("Teamrennen", entry.Context);
        Assert.Contains("Platz 1", entry.Detail);
        Assert.Same(entry, result.Board.OwnEntry);
        Assert.Null(result.Board.NextTarget);
        var challengeCommand = Assert.Single(queryProbe.ReaderCommands, command =>
            command.Contains("RankedChallengeResults", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ROW_NUMBER() OVER", challengeCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChallengeBoardExcludesOpenPlacementAndRunningChallenge()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var bob = context.AddProfile("bob", "Bob Beta");
        var text = context.AddText("Challenge-Text", ratingEligible: true);
        var openPlacement = context.AddChallenge(text.Id, alice.Id, "Offene Platzierung");
        context.AddChallengeResult(openPlacement.Id, alice.Id, 95, 100, ParticipantStatus.Finished, placement: null);
        var running = context.AddChallenge(text.Id, bob.Id, "Laufende Challenge");
        running.Status = ChallengeStatus.Running;
        running.FinishedAt = null;
        context.AddChallengeResult(running.Id, bob.Id, 105, 100, ParticipantStatus.Finished, placement: 1);
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(CompetitionBoardKind.Challenge, CompetitionPeriod.Day, TrainingMode.Sprint60, null));

        Assert.Empty(result.Board.Entries);
        Assert.Null(result.Board.OwnEntry);
        Assert.Null(result.Board.NextTarget);
    }

    [Fact]
    public async Task HiddenCurrentProfileGetsPrivatePreviewOnly()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var hidden = context.AddProfile("hidden", "Hidden Hero", visible: false);
        context.AddAttempt(hidden.Id, TrainingMode.Sprint60, 100, 99, context.Now.AddMinutes(-2));
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(hidden, new LeaderboardQuery(CompetitionBoardKind.Sprint, CompetitionPeriod.Day, TrainingMode.Sprint60, null));

        Assert.Empty(result.Board.Entries);
        Assert.False(result.CurrentProfileVisible);
        Assert.True(result.Board.OwnEntry?.IsPrivatePreview);
        Assert.Equal(100, result.Board.OwnEntry?.Wpm);
    }

    [Fact]
    public async Task SeasonBoardRanksOnlyCurrentSeasonAndHonorsVisibility()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var bob = context.AddProfile("bob", "Bob Beta");
        var hidden = context.AddProfile("hidden", "Hidden Hero", visible: false);
        var current = new Season
        {
            Key = "2026-06",
            Name = "Juni 2026",
            StartsAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var previous = new Season
        {
            Key = "2026-05",
            Name = "Mai 2026",
            StartsAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };
        context.Db.Seasons.AddRange(current, previous);
        context.Db.SeasonScores.AddRange(
            new SeasonScore { SeasonId = current.Id, UserProfileId = alice.Id, Points = 25 },
            new SeasonScore { SeasonId = current.Id, UserProfileId = bob.Id, Points = 40 },
            new SeasonScore { SeasonId = current.Id, UserProfileId = hidden.Id, Points = 99 },
            new SeasonScore { SeasonId = previous.Id, UserProfileId = alice.Id, Points = 500 });
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(alice, new LeaderboardQuery(
            CompetitionBoardKind.Season,
            CompetitionPeriod.AllTime,
            TrainingMode.Sprint60,
            null));

        Assert.Equal([bob.Id, alice.Id], result.Board.Entries.Select(item => item.UserProfileId).ToArray());
        Assert.Equal(CompetitionPeriod.AllTime, result.Query.Period);
        Assert.Equal(40, result.Board.Entries[0].SeasonPoints);
        Assert.Equal(25, result.Board.OwnEntry?.SeasonPoints);
        Assert.DoesNotContain(result.Board.Entries, item => item.UserProfileId == hidden.Id);
    }

    [Fact]
    public async Task AttemptStartMarksStandardModesButNotNonRatingTextsAsLeaderboardEligible()
    {
        await using var context = await CompetitionTestContext.CreateAsync();
        var alice = context.AddProfile("alice", "Alice Alpha");
        var privateText = context.AddText("Privat", ratingEligible: false);
        await context.Db.SaveChangesAsync();
        var attempts = new AttemptService(context.Db, new TypingEngine(context.Time), new MotivationService(context.Db, context.Time), context.Time, new AttemptSessionStore());

        var sprint = await attempts.StartAsync(alice.Id, new StartAttemptRequest(TrainingMode.Sprint60, null, 60, null));
        var text = await attempts.StartAsync(alice.Id, new StartAttemptRequest(TrainingMode.Text, privateText.Id, null, null));

        var sprintAttempt = await context.Db.TypingAttempts.SingleAsync(item => item.Id == sprint.Id);
        var textAttempt = await context.Db.TypingAttempts.SingleAsync(item => item.Id == text.Id);
        Assert.True(sprintAttempt.LeaderboardEligible);
        Assert.False(textAttempt.LeaderboardEligible);
    }

    private sealed class CompetitionTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CompetitionTestContext(SqliteConnection connection, KeyWarsDbContext db, ManualTimeProvider time)
        {
            this.connection = connection;
            Db = db;
            Time = time;
            Service = new CompetitionLeaderboardService(db, time);
        }

        public KeyWarsDbContext Db { get; }
        public ManualTimeProvider Time { get; }
        public CompetitionLeaderboardService Service { get; }
        public DateTimeOffset Now => Time.GetUtcNow();

        public static async Task<CompetitionTestContext> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }

            var options = optionsBuilder.Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new CompetitionTestContext(connection, db, new ManualTimeProvider(DateTimeOffset.Parse("2026-06-27T12:00:00Z")));
        }

        public UserProfile AddProfile(string account, string displayName, bool visible = true, int arenaRating = 1000, string? department = null)
        {
            var profile = new UserProfile
            {
                DisplayName = displayName,
                SamAccountName = account,
                DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
                DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}",
                Department = department,
                LeaderboardVisible = visible,
                ArenaRating = arenaRating,
                ExperiencePoints = arenaRating - 900,
                Level = 2
            };
            Db.UserProfiles.Add(profile);
            return profile;
        }

        public TrainingText AddText(string title, bool ratingEligible)
        {
            var text = new TrainingText
            {
                Title = title,
                SourceKey = $"{title}-{Guid.CreateVersion7():N}",
                Body = "Ein sauberer Wettbewerbstext mit ausreichend Inhalt.",
                Visibility = TrainingTextVisibility.Organization,
                RatingEligible = ratingEligible,
                CharacterCount = 52
            };
            Db.TrainingTexts.Add(text);
            return text;
        }

        public void AddAttempt(
            Guid profileId,
            TrainingMode mode,
            double wpm,
            double accuracy,
            DateTimeOffset finishedAt,
            Guid? textId = null,
            bool leaderboardEligible = true,
            string? textHash = null,
            bool competitionIntegrityEligible = true)
        {
            Db.TypingAttempts.Add(new TypingAttempt
            {
                UserProfileId = profileId,
                TrainingTextId = textId,
                TextHash = textHash ?? (textId is { } id
                    ? TextHash.Compute(Db.TrainingTexts.Local.Single(text => text.Id == id).Body)
                    : string.Empty),
                Mode = mode,
                Phase = AttemptPhase.Finished,
                PreparedAt = finishedAt.AddMinutes(-1),
                StartedAt = finishedAt.AddMinutes(-1),
                FinishedAt = finishedAt,
                DurationMilliseconds = 60_000,
                CorrectCharacters = 400,
                TotalCharacters = 400,
                Wpm = wpm,
                RawWpm = wpm,
                CharactersPerMinute = wpm * 5,
                Accuracy = accuracy,
                Consistency = 95,
                Completed = true,
                Official = true,
                CompetitionIntegrityEligible = competitionIntegrityEligible,
                LeaderboardEligible = leaderboardEligible,
                CreatedAt = finishedAt
            });
        }

        public void AddArenaRoom(
            Guid winnerId,
            Guid runnerUpId,
            bool aborted,
            bool competitionEligible = true)
        {
            var room = new LiveRoomSummary
            {
                Id = Guid.CreateVersion7(),
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                CreatorProfileId = winnerId,
                RoomCode = Guid.CreateVersion7().ToString("N")[..8],
                Mode = LiveRoomMode.Classic,
                Visibility = LiveRoomVisibility.InternalOpen,
                RoundCount = 1,
                CreatedAt = Now.AddMinutes(-20),
                StartedAt = Now.AddMinutes(-10),
                FinishedAt = Now.AddMinutes(-5),
                AbortedByServer = aborted
            };
            Db.LiveRoomSummaries.Add(room);
            Db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary { LiveRoomSummaryId = room.Id, UserProfileId = winnerId, Status = ParticipantStatus.Finished, Placement = 1, Wpm = 88, Accuracy = 99, CompetitionEligible = competitionEligible, RatingBefore = 1000, RatingAfter = 1012 });
            Db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary { LiveRoomSummaryId = room.Id, UserProfileId = runnerUpId, Status = ParticipantStatus.Finished, Placement = 2, Wpm = 74, Accuracy = 98, CompetitionEligible = competitionEligible, RatingBefore = 1000, RatingAfter = 988 });
        }

        public Challenge AddChallenge(Guid textId, Guid creatorId, string title)
        {
            var challenge = new Challenge
            {
                CreatorProfileId = creatorId,
                TrainingTextId = textId,
                Title = title,
                Status = ChallengeStatus.Finished,
                RatingEligible = true,
                CreatedAt = Now.AddHours(-1),
                ExpiresAt = Now.AddDays(1),
                FinishedAt = Now
            };
            Db.Challenges.Add(challenge);
            Db.ChallengeRounds.Add(new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1, CreatedAt = Now.AddHours(-1) });
            return challenge;
        }

        public void AddChallengeResult(
            Guid challengeId,
            Guid profileId,
            double wpm,
            double accuracy,
            ParticipantStatus status,
            int? placement = 1,
            bool competitionEligible = true)
        {
            var round = Db.ChangeTracker.Entries<ChallengeRound>().Single(entry => entry.Entity.ChallengeId == challengeId).Entity;
            Db.ChallengeRoundResults.Add(new ChallengeRoundResult
            {
                ChallengeRoundId = round.Id,
                UserProfileId = profileId,
                Status = status,
                Placement = status == ParticipantStatus.Finished ? placement : null,
                DurationMilliseconds = 60_000,
                Wpm = wpm,
                Accuracy = accuracy,
                Consistency = 95,
                CompetitionEligible = competitionEligible,
                FinishedAt = status == ParticipantStatus.Finished ? Now.AddMinutes(-3) : null
            });
        }

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

    private sealed class LeaderboardQueryProbe : DbCommandInterceptor
    {
        private readonly List<string> readerCommands = [];

        public IReadOnlyList<string> ReaderCommands => readerCommands;

        public void Reset() => readerCommands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            readerCommands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
