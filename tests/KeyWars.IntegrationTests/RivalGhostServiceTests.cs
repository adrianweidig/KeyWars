using System.Text.Json;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace KeyWars.IntegrationTests;

public sealed class RivalGhostServiceTests
{
    [Fact]
    public async Task ReturnsOnlyVisibleOptedInEligibleOfficialResultsFromOtherProfiles()
    {
        await using var context = await RivalGhostTestContext.CreateAsync();
        var owner = context.AddProfile("owner", ghostSharing: false);
        var valid = context.AddProfile("Freigegebene Rivalin", ghostSharing: true);
        var hidden = context.AddProfile("Versteckt", ghostSharing: true, leaderboardVisible: false);
        var optedOut = context.AddProfile("Nicht freigegeben", ghostSharing: false);
        var deleted = context.AddProfile("Gelöscht", ghostSharing: true, deleted: true);
        var unofficial = context.AddProfile("Inoffiziell", ghostSharing: true);
        var ineligible = context.AddProfile("Nicht gewertet", ghostSharing: true);
        var inaccurate = context.AddProfile("Zu ungenau", ghostSharing: true);
        var unfinished = context.AddProfile("Nicht abgeschlossen", ghostSharing: true);
        var text = context.AddText();
        var source = context.AddAttempt(owner.Id, text.Id, 80, 98);

        context.AddAttempt(valid.Id, text.Id, 84, 97);
        context.AddAttempt(hidden.Id, text.Id, 83, 98);
        context.AddAttempt(optedOut.Id, text.Id, 82, 98);
        context.AddAttempt(deleted.Id, text.Id, 81, 98);
        context.AddAttempt(unofficial.Id, text.Id, 86, 98, official: false);
        context.AddAttempt(ineligible.Id, text.Id, 87, 98, leaderboardEligible: false);
        context.AddAttempt(inaccurate.Id, text.Id, 88, 89.9);
        context.AddAttempt(unfinished.Id, text.Id, 89, 99, completed: false, phase: AttemptPhase.Started);
        context.AddAttempt(owner.Id, text.Id, 120, 100);
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(owner.Id, source.Id, valid.Id);

        Assert.NotNull(result);
        var rival = Assert.Single(result.Rivals);
        Assert.Equal(valid.Id, rival.RivalProfileId);
        Assert.Equal(valid.Id, result.SelectedRival?.RivalProfileId);
        Assert.False(result.FallbackApplied);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Nonce", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TextHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Backspaces", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FocusLosses", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TypedState", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keystroke", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsesEachRivalsBestResultAndFallsBackDeterministically()
    {
        var queryProbe = new RivalQueryProbe();
        await using var context = await RivalGhostTestContext.CreateAsync(queryProbe);
        var owner = context.AddProfile("Eigene Person", ghostSharing: false);
        var faster = context.AddProfile("Etwas schneller", ghostSharing: true);
        var slower = context.AddProfile("Etwas langsamer", ghostSharing: true);
        var distant = context.AddProfile("Deutlich schneller", ghostSharing: true);
        var text = context.AddText();
        var source = context.AddAttempt(owner.Id, text.Id, 80, 98);
        context.AddAttempt(faster.Id, text.Id, 85, 97);
        context.AddAttempt(slower.Id, text.Id, 75, 99);
        context.AddAttempt(distant.Id, text.Id, 79, 99);
        context.AddAttempt(distant.Id, text.Id, 96, 98);
        await context.Db.SaveChangesAsync();
        queryProbe.Reset();

        var ownGhost = await context.Service.GetAsync(owner.Id, source.Id);
        var firstFallback = await context.Service.GetAsync(owner.Id, source.Id, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var secondFallback = await context.Service.GetAsync(owner.Id, source.Id, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var selectedSlower = await context.Service.GetAsync(owner.Id, source.Id, slower.Id);

        Assert.NotNull(ownGhost);
        Assert.Null(ownGhost.SelectedRival);
        Assert.False(ownGhost.FallbackApplied);
        Assert.Equal(96, ownGhost.Rivals.Single(rival => rival.RivalProfileId == distant.Id).Wpm);
        Assert.NotNull(firstFallback);
        Assert.True(firstFallback.FallbackApplied);
        Assert.Equal(faster.Id, firstFallback.SelectedRival?.RivalProfileId);
        Assert.Equal(firstFallback.SelectedRival, secondFallback?.SelectedRival);
        Assert.Equal(slower.Id, selectedSlower?.SelectedRival?.RivalProfileId);
        Assert.False(selectedSlower!.FallbackApplied);
        Assert.All(
            queryProbe.ReaderCommands.Where(command =>
                command.Contains("BestPerProfile", StringComparison.OrdinalIgnoreCase)),
            command => Assert.Contains("ROW_NUMBER() OVER", command, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queryProbe.ReaderCommands, command =>
            command.Contains("BestPerProfile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HiddenRequestedRivalFallsBackWithoutDisclosingIt()
    {
        await using var context = await RivalGhostTestContext.CreateAsync();
        var owner = context.AddProfile("Eigene Person", ghostSharing: false);
        var hidden = context.AddProfile("Unsichtbares Profil", ghostSharing: true, leaderboardVisible: false);
        var text = context.AddText();
        var source = context.AddAttempt(owner.Id, text.Id, 80, 98);
        context.AddAttempt(hidden.Id, text.Id, 81, 99);
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(owner.Id, source.Id, hidden.Id);

        Assert.NotNull(result);
        Assert.Empty(result.Rivals);
        Assert.Null(result.SelectedRival);
        Assert.True(result.FallbackApplied);
    }

    [Fact]
    public async Task RejectsForeignUnfinishedAndUnavailableSources()
    {
        await using var context = await RivalGhostTestContext.CreateAsync();
        var owner = context.AddProfile("Eigene Person", ghostSharing: false);
        var other = context.AddProfile("Andere Person", ghostSharing: true);
        var text = context.AddText();
        var foreign = context.AddAttempt(other.Id, text.Id, 80, 98);
        var unfinished = context.AddAttempt(owner.Id, text.Id, 81, 98, completed: false, phase: AttemptPhase.Started);
        var unofficial = context.AddAttempt(owner.Id, text.Id, 82, 99, official: false);
        await context.Db.SaveChangesAsync();

        Assert.Null(await context.Service.GetAsync(owner.Id, foreign.Id));
        Assert.Null(await context.Service.GetAsync(owner.Id, unfinished.Id));
        Assert.Null(await context.Service.GetAsync(owner.Id, unofficial.Id));

        text.IsQuarantined = true;
        await context.Db.SaveChangesAsync();
        var ownFinished = context.AddAttempt(owner.Id, text.Id, 82, 99);
        await context.Db.SaveChangesAsync();
        Assert.Null(await context.Service.GetAsync(owner.Id, ownFinished.Id));
    }

    [Fact]
    public async Task RejectsIntegrityIneligibleSourceAndCandidate()
    {
        await using var context = await RivalGhostTestContext.CreateAsync();
        var owner = context.AddProfile("Eigene Person", ghostSharing: false);
        var valid = context.AddProfile("Gültige Rivalin", ghostSharing: true);
        var ineligible = context.AddProfile("Integritätsfehler", ghostSharing: true);
        var text = context.AddText();
        var invalidSource = context.AddAttempt(
            owner.Id,
            text.Id,
            120,
            99,
            competitionIntegrityEligible: false);
        var validSource = context.AddAttempt(owner.Id, text.Id, 80, 98);
        context.AddAttempt(valid.Id, text.Id, 82, 98);
        context.AddAttempt(
            ineligible.Id,
            text.Id,
            81,
            99,
            competitionIntegrityEligible: false);
        await context.Db.SaveChangesAsync();

        Assert.Null(await context.Service.GetAsync(owner.Id, invalidSource.Id));

        var result = await context.Service.GetAsync(owner.Id, validSource.Id, ineligible.Id);
        var rival = Assert.Single(result!.Rivals);
        Assert.Equal(valid.Id, rival.RivalProfileId);
        Assert.Equal(valid.Id, result.SelectedRival?.RivalProfileId);
        Assert.True(result.FallbackApplied);
    }

    [Fact]
    public async Task BestResultUsesLeaderboardTieBreakWithoutExposingItsAttempt()
    {
        await using var context = await RivalGhostTestContext.CreateAsync();
        var owner = context.AddProfile("Eigene Person", ghostSharing: false);
        var rival = context.AddProfile("Rhythmische Rivalin", ghostSharing: true);
        var text = context.AddText();
        var source = context.AddAttempt(owner.Id, text.Id, 80, 98);
        context.AddAttempt(rival.Id, text.Id, 85, 97, consistency: 88);
        context.AddAttempt(rival.Id, text.Id, 85, 97, consistency: 99);
        await context.Db.SaveChangesAsync();

        var result = await context.Service.GetAsync(owner.Id, source.Id, rival.Id);

        var option = Assert.Single(result!.Rivals);
        Assert.Equal(85, option.Wpm);
        Assert.Equal(97, option.Accuracy);
        Assert.DoesNotContain("Attempt", JsonSerializer.Serialize(option), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RivalGhostTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private RivalGhostTestContext(SqliteConnection connection, KeyWarsDbContext db)
        {
            this.connection = connection;
            Db = db;
            Service = new RivalGhostService(db);
        }

        public KeyWarsDbContext Db { get; }
        public RivalGhostService Service { get; }
        public DateTimeOffset Now { get; } = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

        public static async Task<RivalGhostTestContext> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<KeyWarsDbContext>()
                .UseSqlite(connection);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }

            var options = optionsBuilder.Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new RivalGhostTestContext(connection, db);
        }

        public UserProfile AddProfile(
            string displayName,
            bool ghostSharing,
            bool leaderboardVisible = true,
            bool deleted = false)
        {
            var profile = new UserProfile
            {
                DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
                DirectorySid = $"S-1-5-21-{Guid.NewGuid():N}",
                SamAccountName = $"user-{Guid.NewGuid():N}",
                UserPrincipalName = $"user-{Guid.NewGuid():N}@test.local",
                DisplayName = displayName,
                LeaderboardVisible = leaderboardVisible,
                GhostSharingEnabled = ghostSharing,
                Deleted = deleted
            };
            Db.UserProfiles.Add(profile);
            return profile;
        }

        public TrainingText AddText()
        {
            var text = new TrainingText
            {
                Title = "Freigegebener Standardtext",
                SourceKey = $"rival-test-{Guid.NewGuid():N}",
                Body = "Ein gemeinsamer Text für einen datensparsamen Rivalenvergleich.",
                Visibility = TrainingTextVisibility.Organization,
                IsStandard = true,
                RatingEligible = true,
                CharacterCount = 62,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            Db.TrainingTexts.Add(text);
            return text;
        }

        public TypingAttempt AddAttempt(
            Guid profileId,
            Guid textId,
            double wpm,
            double accuracy,
            bool official = true,
            bool leaderboardEligible = true,
            bool completed = true,
            AttemptPhase phase = AttemptPhase.Finished,
            double consistency = 95,
            bool competitionIntegrityEligible = true)
        {
            var attempt = new TypingAttempt
            {
                UserProfileId = profileId,
                TrainingTextId = textId,
                Mode = TrainingMode.Text,
                Phase = phase,
                PreparedAt = Now.AddMinutes(-2),
                StartedAt = Now.AddMinutes(-1),
                FinishedAt = phase == AttemptPhase.Finished ? Now : null,
                DurationMilliseconds = 60_000,
                CorrectCharacters = 300,
                TotalCharacters = 300,
                Wpm = wpm,
                Accuracy = accuracy,
                Consistency = consistency,
                Completed = completed,
                Official = official,
                CompetitionIntegrityEligible = competitionIntegrityEligible,
                LeaderboardEligible = leaderboardEligible,
                CreatedAt = Now.AddMinutes(-2)
            };
            Db.TypingAttempts.Add(attempt);
            return attempt;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RivalQueryProbe : DbCommandInterceptor
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
