using System.Data.Common;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KeyWars.IntegrationTests;

public sealed class SeasonRolloverTests
{
    [Fact]
    public async Task EnsureCurrentSynchronizesInactiveProfilesAndIsRetrySafeAtBoundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new SeasonCommandProbe();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(commands)
            .Options;
        var active = CreateProfile("Aktiv", seasonPoints: 73);
        var inactive = CreateProfile("Inaktiv", seasonPoints: 41);
        var previous = new Season
        {
            Key = "2026-07",
            Name = "Juli 2026",
            StartsAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            EndsAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z")
        };
        await using (var setup = new KeyWarsDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.UserProfiles.AddRange(active, inactive);
            setup.Seasons.Add(previous);
            setup.SeasonScores.AddRange(
                new SeasonScore { SeasonId = previous.Id, UserProfileId = active.Id, Points = 73 },
                new SeasonScore { SeasonId = previous.Id, UserProfileId = inactive.Id, Points = 41 });
            await setup.SaveChangesAsync();
        }

        commands.Reset();
        await using (var rollover = new KeyWarsDbContext(options))
        {
            var service = new SeasonService(
                rollover,
                new ManualTimeProvider(DateTimeOffset.Parse("2026-08-01T00:00:01Z")));
            var trackedActive = await rollover.UserProfiles.SingleAsync(profile => profile.Id == active.Id);
            var trackedInactive = await rollover.UserProfiles.SingleAsync(profile => profile.Id == inactive.Id);

            await service.EnsureCurrentAsync();
            var rolloverUpdates = commands.ProfileCacheUpdates.ToArray();

            Assert.Equal(0, trackedActive.SeasonPoints);
            Assert.Equal(0, trackedInactive.SeasonPoints);
            Assert.Single(rolloverUpdates);
            Assert.All(rolloverUpdates, command =>
            {
                Assert.Contains("UPDATE UserProfiles", command, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("SeasonScores", command, StringComparison.OrdinalIgnoreCase);
            });
        }

        commands.Reset();
        await using (var retry = new KeyWarsDbContext(options))
        {
            await new SeasonService(
                    retry,
                    new ManualTimeProvider(DateTimeOffset.Parse("2026-08-01T00:00:01Z")))
                .EnsureCurrentAsync();
            Assert.Empty(commands.MutatingCommands);
        }

        await using var verification = new KeyWarsDbContext(options);
        Assert.Equal(0, await verification.UserProfiles
            .Where(profile => profile.Id == active.Id)
            .Select(profile => profile.SeasonPoints)
            .SingleAsync());
        Assert.Equal(0, await verification.UserProfiles
            .Where(profile => profile.Id == inactive.Id)
            .Select(profile => profile.SeasonPoints)
            .SingleAsync());
        Assert.Equal(73, await verification.SeasonScores
            .Where(score => score.SeasonId == previous.Id && score.UserProfileId == active.Id)
            .Select(score => score.Points)
            .SingleAsync());
        Assert.Equal(2, await verification.Seasons.CountAsync());
    }

    [Fact]
    public async Task ParallelFirstAwardAndEnsureCreateOneSeasonWithoutLosingPoints()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"keywars-season-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30;Pooling=False";
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var now = DateTimeOffset.Parse("2026-08-01T00:00:01Z");
        var active = CreateProfile("Parallel aktiv", seasonPoints: 73);
        var inactive = CreateProfile("Parallel inaktiv", seasonPoints: 41);
        var previous = new Season
        {
            Key = "2026-07",
            Name = "Juli 2026",
            StartsAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            EndsAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z")
        };

        try
        {
            await using (var setup = new KeyWarsDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                setup.UserProfiles.AddRange(active, inactive);
                setup.Seasons.Add(previous);
                setup.SeasonScores.AddRange(
                    new SeasonScore { SeasonId = previous.Id, UserProfileId = active.Id, Points = 73 },
                    new SeasonScore { SeasonId = previous.Id, UserProfileId = inactive.Id, Points = 41 });
                await setup.SaveChangesAsync();
            }

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var award = Task.Run(async () =>
            {
                await ready.Task;
                await using var awardDb = new KeyWarsDbContext(options);
                var profile = await awardDb.UserProfiles.SingleAsync(item => item.Id == active.Id);
                var service = new SeasonService(awardDb, new ManualTimeProvider(now));
                Assert.Equal(17, await service.AwardAsync(profile, "parallel", "first", 17, now));
                await awardDb.SaveChangesAsync();
            });
            var ensure = Task.Run(async () =>
            {
                await ready.Task;
                await using var ensureDb = new KeyWarsDbContext(options);
                await new SeasonService(ensureDb, new ManualTimeProvider(now)).EnsureCurrentAsync();
            });

            ready.SetResult();
            await Task.WhenAll(award, ensure);

            await using var verification = new KeyWarsDbContext(options);
            var current = await verification.Seasons.SingleAsync(season => season.Key == "2026-08");
            Assert.Equal(2, await verification.Seasons.CountAsync());
            Assert.Equal(17, await verification.SeasonScores
                .Where(score => score.SeasonId == current.Id && score.UserProfileId == active.Id)
                .Select(score => score.Points)
                .SingleAsync());
            Assert.Equal(17, await verification.UserProfiles
                .Where(profile => profile.Id == active.Id)
                .Select(profile => profile.SeasonPoints)
                .SingleAsync());
            Assert.Equal(0, await verification.UserProfiles
                .Where(profile => profile.Id == inactive.Id)
                .Select(profile => profile.SeasonPoints)
                .SingleAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static UserProfile CreateProfile(string displayName, int seasonPoints) => new()
    {
        DisplayName = displayName,
        SamAccountName = $"season-{Guid.NewGuid():N}",
        DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
        DirectorySid = $"S-1-5-21-{Guid.NewGuid():N}",
        SeasonPoints = seasonPoints
    };

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class SeasonCommandProbe : DbCommandInterceptor
    {
        private readonly List<string> profileCacheUpdates = [];
        private readonly List<string> mutatingCommands = [];

        public IReadOnlyList<string> ProfileCacheUpdates => profileCacheUpdates;
        public IReadOnlyList<string> MutatingCommands => mutatingCommands;

        public void Reset()
        {
            profileCacheUpdates.Clear();
            mutatingCommands.Clear();
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            mutatingCommands.Add(command.CommandText);
            if (command.CommandText.Contains("UPDATE UserProfiles", StringComparison.OrdinalIgnoreCase))
            {
                profileCacheUpdates.Add(command.CommandText);
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
