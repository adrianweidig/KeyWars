using System.Data.Common;
using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Pages;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KeyWars.IntegrationTests;

public sealed class DashboardReadPathTests
{
    [Fact]
    public async Task SteadyStateDashboardUsesNarrowReadsWithoutWriteLocksOrUnusedModels()
    {
        var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var probe = new DashboardCommandProbe();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(probe)
            .Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DisplayName = "Narrow Dashboard",
            SamAccountName = "dashboard-narrow",
            DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
            DirectorySid = $"S-1-5-21-{Guid.NewGuid():N}"
        };
        db.UserProfiles.Add(profile);
        db.TypingAttempts.Add(new TypingAttempt
        {
            UserProfileId = profile.Id,
            Mode = TrainingMode.Sprint60,
            Phase = AttemptPhase.Finished,
            PreparedAt = now.AddMinutes(-2),
            StartedAt = now.AddMinutes(-1),
            FinishedAt = now,
            DurationMilliseconds = 60_000,
            CorrectCharacters = 300,
            TotalCharacters = 300,
            Wpm = 72,
            Accuracy = 99,
            Consistency = 95,
            Completed = true,
            Official = true,
            LeaderboardEligible = true,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        var time = new ManualTimeProvider(now);
        var motivation = new MotivationService(db, time);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        await motivation.EnsureCurrentMissionsAsync(profile.Id, today);
        probe.Reset();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(KeyWarsClaims.ProfileId, profile.Id.ToString("D"))],
                "Test"))
        };
        var page = new IndexModel(
            new CurrentUser(db),
            db,
            motivation,
            new ProfileInsightsService(db, time),
            new CompetitionLeaderboardService(db, time),
            time)
        {
            PageContext = new PageContext { HttpContext = httpContext }
        };

        await page.OnGetAsync(CancellationToken.None);

        Assert.Single(page.RecentResults);
        Assert.Single(page.DailySprintBoard.Entries);
        Assert.Equal(4, probe.ReaderCommands.Count);
        Assert.Empty(probe.NonQueryCommands);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.DoesNotContain(probe.ReaderCommands, command =>
            ReferencesTable(command, "Challenges") ||
            ReferencesTable(command, "WeaknessObservations") ||
            ReferencesTable(command, "Achievements") ||
            ReferencesTable(command, "GamificationEvents"));
        Assert.DoesNotContain(
            typeof(IndexModel).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(ChallengeService));
        Assert.DoesNotContain(
            typeof(IndexModel).GetProperties().Select(property => property.Name),
            property => property is "Challenges" or "Insights" or "Recommendation" or "LevelProgress");
    }

    private static bool ReferencesTable(string command, string table) =>
        command.Contains($"FROM \"{table}\"", StringComparison.OrdinalIgnoreCase) ||
        command.Contains($"JOIN \"{table}\"", StringComparison.OrdinalIgnoreCase);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class DashboardCommandProbe : DbCommandInterceptor
    {
        private readonly List<string> readerCommands = [];
        private readonly List<string> nonQueryCommands = [];

        public IReadOnlyList<string> ReaderCommands => readerCommands;
        public IReadOnlyList<string> NonQueryCommands => nonQueryCommands;

        public void Reset()
        {
            readerCommands.Clear();
            nonQueryCommands.Clear();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            readerCommands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            nonQueryCommands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
