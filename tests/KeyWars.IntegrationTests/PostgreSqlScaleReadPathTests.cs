using System.Data.Common;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace KeyWars.IntegrationTests;

public sealed class PostgreSqlScaleReadPathTests
{
    [PostgreSqlFact]
    public async Task PostgreSqlScaleReadPathsAndSeasonRolloverUseNativeWindowQueries()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = $"keywars_scale_{Guid.NewGuid():N}";
        var scopedConnectionString = await CreateSchemaAsync(connectionString, schema);
        try
        {
            var options = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using var db = new PostgresKeyWarsDbContext(options);
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());

            var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
            var owner = CreateProfile("Owner", seasonPoints: 27, ghostSharing: false);
            var rival = CreateProfile("Rival", seasonPoints: 63, ghostSharing: true);
            var inactive = CreateProfile("Inactive", seasonPoints: 41, ghostSharing: false);
            var text = new TrainingText
            {
                Title = "PostgreSQL Scale Text",
                SourceKey = "postgres-scale-text",
                Body = "Ein gemeinsamer Text für providerbewusste Ranglistenabfragen.",
                Visibility = TrainingTextVisibility.Organization,
                IsStandard = true,
                RatingEligible = true,
                CharacterCount = 65,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AddRange(owner, rival, inactive, text);

            var source = AddAttempt(db, owner.Id, text.Id, TrainingMode.Text, 80, 99, now.AddMinutes(-5));
            AddAttempt(db, rival.Id, text.Id, TrainingMode.Text, 84, 99, now.AddMinutes(-4));
            AddAttempt(db, rival.Id, text.Id, TrainingMode.Text, 96, 98, now.AddMinutes(-3));
            AddAttempt(db, owner.Id, null, TrainingMode.Sprint60, 70, 99, now.AddMinutes(-2));
            AddAttempt(db, owner.Id, null, TrainingMode.Sprint60, 75, 98, now.AddMinutes(-1));

            var challenge = new Challenge
            {
                CreatorProfileId = owner.Id,
                TrainingTextId = text.Id,
                Title = "PostgreSQL Challenge",
                Status = ChallengeStatus.Finished,
                RatingEligible = true,
                CreatedAt = now.AddHours(-1),
                ExpiresAt = now.AddDays(1),
                FinishedAt = now
            };
            var firstRound = new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1, CreatedAt = now.AddMinutes(-20) };
            var secondRound = new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 2, CreatedAt = now.AddMinutes(-10) };
            db.AddRange(challenge, firstRound, secondRound);
            db.ChallengeRoundResults.AddRange(
                ChallengeResult(firstRound.Id, owner.Id, 72, now.AddMinutes(-19)),
                ChallengeResult(secondRound.Id, owner.Id, 82, now.AddMinutes(-9)));

            var room = new LiveRoomSummary
            {
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                CreatorProfileId = owner.Id,
                RoomCode = "PGSCALE1",
                Mode = LiveRoomMode.Classic,
                Visibility = LiveRoomVisibility.InternalOpen,
                RoundCount = 1,
                CreatedAt = now.AddMinutes(-15),
                StartedAt = now.AddMinutes(-10),
                FinishedAt = now.AddMinutes(-5)
            };
            db.LiveRoomSummaries.Add(room);
            db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
            {
                LiveRoomSummaryId = room.Id,
                UserProfileId = owner.Id,
                Status = ParticipantStatus.Finished,
                Placement = 1,
                Wpm = 88,
                Accuracy = 99,
                RatingBefore = 1000,
                RatingAfter = 1012
            });

            var season = new Season
            {
                Key = "2026-07",
                Name = "Juli 2026",
                StartsAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                EndsAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z")
            };
            db.Seasons.Add(season);
            db.SeasonScores.Add(new SeasonScore
            {
                SeasonId = season.Id,
                UserProfileId = owner.Id,
                Points = 17,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();

            var time = new ManualTimeProvider(now);
            var competition = new CompetitionLeaderboardService(db, time);
            var sprint = await competition.GetBoardAsync(owner, new LeaderboardQuery(
                CompetitionBoardKind.Sprint,
                CompetitionPeriod.Day,
                TrainingMode.Sprint60,
                null));
            var challengeBoard = await competition.GetBoardAsync(owner, new LeaderboardQuery(
                CompetitionBoardKind.Challenge,
                CompetitionPeriod.Day,
                TrainingMode.Sprint60,
                null));
            var arena = await competition.GetBoardAsync(owner, new LeaderboardQuery(
                CompetitionBoardKind.ArenaRating,
                CompetitionPeriod.Day,
                TrainingMode.Sprint60,
                null));
            var ghost = await new RivalGhostService(db).GetAsync(owner.Id, source.Id, rival.Id);
            var seasons = new SeasonService(db, time);
            await seasons.EnsureCurrentAsync();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                Assert.Equal(17, await seasons.AwardAsync(owner, "postgres-scale", "rollover", 17, now));
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            await seasons.EnsureCurrentAsync();
            db.ChangeTracker.Clear();

            Assert.Equal(75, Assert.Single(sprint.Entries).Wpm);
            Assert.Equal(82, Assert.Single(challengeBoard.Entries).Wpm);
            Assert.Equal(1, arena.Entries.Single(entry => entry.UserProfileId == owner.Id).Attempts);
            Assert.Equal(96, Assert.Single(ghost!.Rivals).Wpm);
            Assert.Equal(17, await db.UserProfiles.Where(profile => profile.Id == owner.Id)
                .Select(profile => profile.SeasonPoints).SingleAsync());
            Assert.Equal(0, await db.UserProfiles.Where(profile => profile.Id == inactive.Id)
                .Select(profile => profile.SeasonPoints).SingleAsync());
        }
        finally
        {
            await DropSchemaAsync(connectionString, schema);
        }
    }

    [PostgreSqlFact]
    public async Task PostgreSqlRolloverFencesHistoricAwardAndKeepsCurrentCacheAtZero()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = $"keywars_season_fence_{Guid.NewGuid():N}";
        var scopedConnectionString = await CreateSchemaAsync(connectionString, schema);
        try
        {
            var baseOptions = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            var oldTime = DateTimeOffset.Parse("2026-07-31T23:59:59Z");
            var newTime = DateTimeOffset.Parse("2026-08-01T00:00:01Z");
            var profile = CreateProfile("Season Fence", seasonPoints: 100, ghostSharing: false);
            var july = new Season
            {
                Key = "2026-07",
                Name = "Juli 2026",
                StartsAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                EndsAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z")
            };
            await using (var setup = new PostgresKeyWarsDbContext(baseOptions))
            {
                await setup.Database.ExecuteSqlRawAsync(setup.Database.GenerateCreateScript());
                setup.AddRange(profile, july);
                setup.SeasonScores.Add(new SeasonScore
                {
                    SeasonId = july.Id,
                    UserProfileId = profile.Id,
                    Points = 100,
                    UpdatedAt = oldTime
                });
                await setup.SaveChangesAsync();
            }

            var rolloverProbe = new SeasonFenceCommandProbe(pauseAfterAcquisition: true);
            var awardProbe = new SeasonFenceCommandProbe(pauseAfterAcquisition: false);
            var rolloverOptions = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .AddInterceptors(rolloverProbe)
                .Options;
            var awardOptions = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
                .UseNpgsql(scopedConnectionString)
                .AddInterceptors(awardProbe)
                .Options;
            await using var rolloverDb = new PostgresKeyWarsDbContext(rolloverOptions);
            await using var awardDb = new PostgresKeyWarsDbContext(awardOptions);
            var staleProfile = await awardDb.UserProfiles.SingleAsync(item => item.Id == profile.Id);
            var awardTime = new ManualTimeProvider(oldTime);
            await using var awardTransaction = await awardDb.Database.BeginTransactionAsync();

            var rolloverTask = new SeasonService(
                    rolloverDb,
                    new ManualTimeProvider(newTime))
                .EnsureCurrentAsync();
            Task awardTask = Task.CompletedTask;
            try
            {
                await rolloverProbe.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                awardTask = Task.Run(async () =>
                {
                    var seasons = new SeasonService(awardDb, awardTime);
                    Assert.Equal(10, await seasons.AwardAsync(
                        staleProfile,
                        "season-fence",
                        "historic-award",
                        10,
                        oldTime));
                    await awardDb.SaveChangesAsync();
                    await awardTransaction.CommitAsync();
                });

                await awardProbe.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(awardTask.IsCompleted);
                awardTime.SetUtcNow(newTime);
                rolloverProbe.Release();
                await rolloverTask.WaitAsync(TimeSpan.FromSeconds(10));
                await awardTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                rolloverProbe.Release();
                try
                {
                    await Task.WhenAll(rolloverTask, awardTask).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Preserve the assertion or persistence failure from the test body.
                }
            }

            await using var verification = new PostgresKeyWarsDbContext(baseOptions);
            var august = await verification.Seasons.SingleAsync(item => item.Key == "2026-08");
            Assert.Equal(0, await verification.UserProfiles
                .Where(item => item.Id == profile.Id)
                .Select(item => item.SeasonPoints)
                .SingleAsync());
            Assert.Equal(110, await verification.SeasonScores
                .Where(item => item.SeasonId == july.Id && item.UserProfileId == profile.Id)
                .Select(item => item.Points)
                .SingleAsync());
            Assert.False(await verification.SeasonScores
                .AnyAsync(item => item.SeasonId == august.Id && item.UserProfileId == profile.Id));
        }
        finally
        {
            await DropSchemaAsync(connectionString, schema);
        }
    }

    private static UserProfile CreateProfile(string name, int seasonPoints, bool ghostSharing) => new()
    {
        DisplayName = name,
        SamAccountName = $"pg-{Guid.NewGuid():N}",
        DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
        DirectorySid = $"S-1-5-21-{Guid.NewGuid():N}",
        LeaderboardVisible = true,
        GhostSharingEnabled = ghostSharing,
        SeasonPoints = seasonPoints
    };

    private static TypingAttempt AddAttempt(
        KeyWarsDbContext db,
        Guid profileId,
        Guid? textId,
        TrainingMode mode,
        double wpm,
        double accuracy,
        DateTimeOffset finishedAt)
    {
        var attempt = new TypingAttempt
        {
            UserProfileId = profileId,
            TrainingTextId = textId,
            Mode = mode,
            Phase = AttemptPhase.Finished,
            PreparedAt = finishedAt.AddMinutes(-1),
            StartedAt = finishedAt.AddMinutes(-1),
            FinishedAt = finishedAt,
            DurationMilliseconds = 60_000,
            CorrectCharacters = 300,
            TotalCharacters = 300,
            Wpm = wpm,
            Accuracy = accuracy,
            Consistency = 95,
            Completed = true,
            Official = true,
            LeaderboardEligible = true,
            CreatedAt = finishedAt
        };
        db.TypingAttempts.Add(attempt);
        return attempt;
    }

    private static ChallengeRoundResult ChallengeResult(
        Guid roundId,
        Guid profileId,
        double wpm,
        DateTimeOffset finishedAt) => new()
        {
            ChallengeRoundId = roundId,
            UserProfileId = profileId,
            Status = ParticipantStatus.Finished,
            Placement = 1,
            DurationMilliseconds = 60_000,
            Wpm = wpm,
            Accuracy = 99,
            Consistency = 95,
            FinishedAt = finishedAt
        };

    private static async Task<string> CreateSchemaAsync(string connectionString, string schema)
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

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return utcNow;
            }
        }

        public void SetUtcNow(DateTimeOffset value)
        {
            lock (gate)
            {
                utcNow = value;
            }
        }
    }

    private sealed class SeasonFenceCommandProbe(bool pauseAfterAcquisition) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => release.TrySetResult();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsSeasonFence(command))
            {
                Requested.TrySetResult();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsSeasonFence(command))
            {
                Acquired.TrySetResult();
                if (pauseAfterAcquisition)
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
            }

            return result;
        }

        private static bool IsSeasonFence(DbCommand command) =>
            command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase);
    }
}
