using System.Text;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeyWars.IntegrationTests;

public sealed class ProfileAndPersistenceTests
{
    [Fact]
    public async Task ProvisioningUsesDirectoryGuidAsStableKey()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var provisioner = new ProfileProvisioner(db, TimeProvider.System);
        var identity = new DirectoryIdentity("11111111-1111-1111-1111-111111111111", "S-1-5-21-1", "mmustermann", "mmustermann@example.local", "Max Mustermann", "Max", "Mustermann", "max@example.local", "IT", "Trainer");

        var first = await provisioner.ProvisionAsync(identity, CancellationToken.None);
        var second = await provisioner.ProvisionAsync(identity with { DisplayName = "Max M." }, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Max M.", second.DisplayName);
        Assert.Equal(1, await db.UserProfiles.CountAsync());
    }

    [Fact]
    public async Task PersonSearchOnlyReturnsLocalProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.UserProfiles.AddRange(
            new UserProfile { DisplayName = "Anna Beispiel", SamAccountName = "abeispiel", DirectoryObjectGuid = Guid.NewGuid().ToString(), DirectorySid = "S-1" },
            new UserProfile { DisplayName = "Bernd Beispiel", SamAccountName = "bbeispiel", DirectoryObjectGuid = Guid.NewGuid().ToString(), DirectorySid = "S-2" });
        await db.SaveChangesAsync();
        var service = new TextLibraryService(db, new CurrentUser(db), Microsoft.Extensions.Options.Options.Create(new KeyWars.Services.ContentOptions()));
        var current = await db.UserProfiles.SingleAsync(profile => profile.SamAccountName == "abeispiel");

        var result = await service.SearchPeopleAsync(current.Id, "Bernd");

        Assert.Single(result);
        Assert.Equal("Bernd Beispiel", result[0].DisplayName);
    }

    [Fact]
    public async Task AttemptSessionSurvivesRequestScopedServiceInstances()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        AttemptSession session;
        await using (var db = new KeyWarsDbContext(context.Options))
        {
            var starter = context.CreateService(db);
            session = await starter.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        }

        await using (var db = new KeyWarsDbContext(context.Options))
        {
            var finisher = context.CreateService(db);
            await finisher.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
            context.Time.Advance(TimeSpan.FromSeconds(5));
            var attempt = await finisher.FinishAsync(
                context.ProfileId,
                new FinishAttemptRequest(session.Id, session.Text, 0, 0, 5000) { Nonce = session.Nonce });

            Assert.True(attempt.Completed);
            Assert.Equal(session.Id, attempt.Id);
            Assert.NotNull(attempt.FinishedAt);
            Assert.Equal(AttemptPhase.Finished, attempt.Phase);
        }
    }

    [Fact]
    public async Task PreparedDelayDoesNotCountTowardAuthoritativeDuration()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        var preparedAt = context.Time.GetUtcNow();
        context.Time.Advance(TimeSpan.FromSeconds(30));
        var begin = await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(5));

        var attempt = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 35000) { Nonce = session.Nonce });

        Assert.Equal(preparedAt, attempt.PreparedAt);
        Assert.Equal(begin.StartedAt, attempt.StartedAt);
        Assert.Equal(5000, attempt.DurationMilliseconds);
        Assert.Equal(35000, attempt.ClientDurationMilliseconds);
        Assert.StartsWith("sha256:", attempt.TextHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SprintBeginUsesModeForDeadlineAndIgnoresCompatibilitySeconds()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(
            context.ProfileId,
            new StartAttemptRequest(TrainingMode.Sprint60, null, 1, 120));
        context.Time.Advance(TimeSpan.FromSeconds(7));
        var begin = await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));

        Assert.Equal(session.Id, begin.AttemptId);
        Assert.Equal(120, TypingEngine.CountWords(session.Text));
        Assert.Equal(context.Time.GetUtcNow(), begin.StartedAt);
        Assert.Equal(begin.StartedAt.AddSeconds(60), begin.EndsAt);
        Assert.Equal(context.Time.GetUtcNow(), begin.ServerNow);

        context.Time.Advance(TimeSpan.FromSeconds(3));
        var replay = await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        Assert.Equal(begin.StartedAt, replay.StartedAt);
        Assert.Equal(begin.EndsAt, replay.EndsAt);
        Assert.Equal(context.Time.GetUtcNow(), replay.ServerNow);
    }

    [Theory]
    [InlineData(TrainingMode.Words10, 10)]
    [InlineData(TrainingMode.Words25, 25)]
    [InlineData(TrainingMode.Words50, 50)]
    [InlineData(TrainingMode.Words100, 100)]
    public async Task WordModesDeriveTheirTargetSizeFromTheMode(TrainingMode mode, int expectedWordCount)
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(
            context.ProfileId,
            new StartAttemptRequest(mode, null, null, null));
        var stored = await db.TypingAttempts.SingleAsync(item => item.Id == session.Id);

        Assert.Equal(expectedWordCount, TypingEngine.CountWords(session.Text));
        Assert.Null(stored.TrainingTextId);
        Assert.True(stored.LeaderboardEligible);
    }

    [Fact]
    public async Task StartRejectsModeIncompatibleTargetParameters()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);
        var requests = new[]
        {
            new StartAttemptRequest(TrainingMode.Words100, null, null, 10),
            new StartAttemptRequest(TrainingMode.Sprint60, Guid.CreateVersion7(), 60, 120),
            new StartAttemptRequest(TrainingMode.Sprint60, null, 60, 80),
            new StartAttemptRequest(TrainingMode.Text, null, null, 80)
        };

        foreach (var request in requests)
        {
            var error = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
                service.StartAsync(context.ProfileId, request));
            Assert.Equal((AttemptErrorCodes.InvalidRequest, 400), (error.Code, error.StatusCode));
        }

        Assert.Empty(await db.TypingAttempts.ToListAsync());
    }

    [Fact]
    public async Task InvalidNonceDoesNotConsumeActiveSession()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(4));

        var error = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            service.FinishAsync(
                context.ProfileId,
                new FinishAttemptRequest(session.Id, session.Text, 0, 0, 4000) { Nonce = "bad-nonce" }));

        Assert.Equal(AttemptErrorCodes.InvalidNonce, error.Code);
        Assert.Equal(409, error.StatusCode);

        var attempt = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 4000) { Nonce = session.Nonce });

        Assert.True(attempt.Completed);
        Assert.Equal(AttemptPhase.Finished, attempt.Phase);
    }

    [Fact]
    public async Task AttemptLifecycleUsesStableErrorCodesAndStatuses()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var invalid = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            service.StartAsync(context.ProfileId, new StartAttemptRequest((TrainingMode)999, null, null, null)));
        Assert.Equal((AttemptErrorCodes.InvalidRequest, 400), (invalid.Code, invalid.StatusCode));

        var missing = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            service.FinishAsync(
                context.ProfileId,
                new FinishAttemptRequest(Guid.CreateVersion7(), "", 0, 0, 0) { Nonce = "missing" }));
        Assert.Equal((AttemptErrorCodes.NotFound, 404), (missing.Code, missing.StatusCode));

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        var notStarted = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            service.FinishAsync(
                context.ProfileId,
                new FinishAttemptRequest(session.Id, session.Text, 0, 0, 0) { Nonce = session.Nonce }));
        Assert.Equal((AttemptErrorCodes.NotStarted, 409), (notStarted.Code, notStarted.StatusCode));

        context.Time.Advance(TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(1)));
        var expired = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce)));
        Assert.Equal((AttemptErrorCodes.Expired, 410), (expired.Code, expired.StatusCode));
    }

    [Fact]
    public async Task FinishedAttemptReplayReturnsPersistedResultWithoutDuplicateXp()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(8));
        var request = new FinishAttemptRequest(session.Id, session.Text, 0, 0, 8000) { Nonce = session.Nonce };

        var first = await service.FinishAsync(context.ProfileId, request);
        var xpAfterFirstFinish = await db.UserProfiles.Where(profile => profile.Id == context.ProfileId).Select(profile => profile.ExperiencePoints).SingleAsync();
        var replay = await service.FinishAsync(context.ProfileId, request);
        var xpAfterReplay = await db.UserProfiles.Where(profile => profile.Id == context.ProfileId).Select(profile => profile.ExperiencePoints).SingleAsync();

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Wpm, replay.Wpm);
        Assert.Equal(xpAfterFirstFinish, xpAfterReplay);
    }

    [Fact]
    public async Task TimedSprintPartialInputCompletesAfterServerDurationLimit()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Sprint60, null, 60, 120));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(60));
        var partialInput = string.Concat(TypingEngine.SplitGraphemes(session.Text).Take(20));

        var attempt = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, partialInput, 0, 0, 60000) { Nonce = session.Nonce });
        var profileXp = await db.UserProfiles.Where(profile => profile.Id == context.ProfileId).Select(profile => profile.ExperiencePoints).SingleAsync();

        Assert.True(attempt.Completed);
        Assert.Equal(60000, attempt.DurationMilliseconds);
        Assert.True(profileXp > 0);
    }

    [Fact]
    public async Task TimedSprintRejectsEarlyPartialInputWithoutMutation()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Sprint60, null, 5, 120));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        var partialInput = string.Concat(TypingEngine.SplitGraphemes(session.Text).Take(20));

        var error = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            service.FinishAsync(
                context.ProfileId,
                new FinishAttemptRequest(session.Id, partialInput, 0, 0, 10_000) { Nonce = session.Nonce }));

        Assert.Equal(AttemptErrorCodes.StillRunning, error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(50_000, error.RetryAfterMs);
        var stored = await db.TypingAttempts.SingleAsync(item => item.Id == session.Id);
        Assert.Equal(AttemptPhase.Started, stored.Phase);
        Assert.Null(stored.FinishedAt);
        Assert.Empty(await db.TypingAttemptErrors.Where(item => item.TypingAttemptId == session.Id).ToListAsync());
        Assert.Empty(await db.RewardLedgerEntries.Where(item => item.SourceId == session.Id.ToString("N")).ToListAsync());
        Assert.Empty(await db.GamificationEvents.Where(item => item.SourceId == session.Id.ToString("N")).ToListAsync());

        context.Time.Advance(TimeSpan.FromSeconds(50));
        var completion = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, partialInput, 0, 0, 60_000) { Nonce = session.Nonce });
        Assert.True(completion.Completed);
        Assert.Equal(60_000, completion.DurationMilliseconds);
    }

    [Fact]
    public async Task TimedSprintAcceptsExactNormalizedGraphemeSequenceBeforeDeadline()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Sprint60, null, 60, 120));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(2));

        var completion = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, session.Text.Normalize(NormalizationForm.FormD), 0, 0, 2_000) { Nonce = session.Nonce });

        Assert.True(completion.Completed);
        Assert.Equal(2_000, completion.DurationMilliseconds);
        Assert.Equal(100d, completion.Accuracy);
    }

    [Fact]
    public async Task ParallelFinishReturnsCanonicalResultAndAwardsAttemptXpOnce()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        AttemptSession session;
        await using (var setupDb = new KeyWarsDbContext(context.Options))
        {
            var setup = context.CreateService(setupDb);
            session = await setup.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
            await setup.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        }

        context.Time.Advance(TimeSpan.FromSeconds(8));
        var request = new FinishAttemptRequest(session.Id, session.Text, 0, 0, 8_000) { Nonce = session.Nonce };
        await using var firstDb = new KeyWarsDbContext(context.Options);
        await using var secondDb = new KeyWarsDbContext(context.Options);
        var results = await Task.WhenAll(
            context.CreateService(firstDb).FinishAsync(context.ProfileId, request),
            context.CreateService(secondDb).FinishAsync(context.ProfileId, request));

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(results[0].Wpm, results[1].Wpm);
        Assert.Equal(results[0].Motivation.XpDelta, results[1].Motivation.XpDelta);
        Assert.Equal(
            results[0].Motivation.Events.Select(item => item.Id),
            results[1].Motivation.Events.Select(item => item.Id));
        await using var verificationDb = new KeyWarsDbContext(context.Options);
        Assert.Single(await verificationDb.RewardLedgerEntries
            .Where(item => item.UserProfileId == context.ProfileId && item.Source == "attempt" && item.SourceId == session.Id.ToString("N"))
            .ToListAsync());
        Assert.Equal(AttemptPhase.Finished, await verificationDb.TypingAttempts
            .Where(item => item.Id == session.Id)
            .Select(item => item.Phase)
            .SingleAsync());

        var canonicalEventIds = results[0].Motivation.Events.Select(item => item.Id).ToArray();
        var canonicalCreatedAt = results[0].Motivation.Events.First().CreatedAt;
        verificationDb.GamificationEvents.Add(new GamificationEvent
        {
            UserProfileId = context.ProfileId,
            Type = GamificationEventType.XpAwarded,
            EventKey = "xp-awarded",
            Title = "+999 XP",
            Description = "Gleichzeitiges anderes Ergebnis",
            XpDelta = 999,
            LevelBefore = 1,
            LevelAfter = 1,
            Source = "attempt",
            SourceId = Guid.CreateVersion7().ToString("N"),
            CreatedAt = canonicalCreatedAt
        });
        await verificationDb.SaveChangesAsync();

        var replay = await context.CreateService(verificationDb)
            .FinishAsync(context.ProfileId, request);
        Assert.Equal(results[0].Motivation.XpDelta, replay.Motivation.XpDelta);
        Assert.Equal(canonicalEventIds, replay.Motivation.Events.Select(item => item.Id));
    }

    [Fact]
    public async Task FinishRollsBackAttemptAndMotivationWhenPersistenceFails()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);
        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(8));
        var request = new FinishAttemptRequest(session.Id, session.Text, 0, 0, 8_000) { Nonce = session.Nonce };
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_attempt_reward
            BEFORE INSERT ON RewardLedgerEntries
            WHEN NEW.Source = 'attempt'
            BEGIN
                SELECT RAISE(ABORT, 'forced reward failure');
            END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.FinishAsync(context.ProfileId, request));

        var rolledBack = await db.TypingAttempts.AsNoTracking().SingleAsync(item => item.Id == session.Id);
        Assert.Equal(AttemptPhase.Started, rolledBack.Phase);
        Assert.Null(rolledBack.FinishedAt);
        Assert.Empty(await db.RewardLedgerEntries.Where(item => item.UserProfileId == context.ProfileId).ToListAsync());
        Assert.Empty(await db.GamificationEvents.Where(item => item.UserProfileId == context.ProfileId).ToListAsync());

        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_attempt_reward;");
        var completion = await service.FinishAsync(context.ProfileId, request);
        Assert.Equal(AttemptPhase.Finished, completion.Phase);
        Assert.Single(await db.RewardLedgerEntries
            .Where(item => item.Source == "attempt" && item.SourceId == session.Id.ToString("N"))
            .ToListAsync());
    }

    [Fact]
    public async Task FinishPersistsWordTimingAndActualErrorPatternsOnly()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Sprint60, null, 60, 120));
        var targetElements = TypingEngine.SplitGraphemes(session.Text).ToArray();
        var errorIndex = Array.FindIndex(targetElements, element => !string.IsNullOrWhiteSpace(element));
        Assert.InRange(errorIndex, 0, targetElements.Length - 2);
        var expected = targetElements[errorIndex];
        var expectedPattern = targetElements[errorIndex] + targetElements[errorIndex + 1];
        targetElements[errorIndex] = "§";
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(60));
        var attempt = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, string.Concat(targetElements), 0, 0, 60_000)
            {
                Nonce = session.Nonce,
                WordDurationsMilliseconds = [1000, 1500, 800]
            });

        var error = await db.TypingAttemptErrors.SingleAsync(item => item.TypingAttemptId == attempt.Id);
        var observations = await db.WeaknessObservations.Where(item => item.UserProfileId == context.ProfileId).ToListAsync();

        Assert.Equal(1, attempt.IncorrectCharacters);
        Assert.Equal(3, attempt.ConsistencySampleCount);
        Assert.True(attempt.WordTimingVariation > 0);
        Assert.Equal(TypingErrorKind.Substitution, error.Kind);
        Assert.Equal(expected, error.Expected);
        Assert.Equal("§", error.Actual);
        Assert.Contains(observations, item => item.Pattern == expected && item.Errors == 1);
        Assert.Contains(observations, item => item.Pattern == expectedPattern && item.Errors == 1);
        Assert.DoesNotContain(observations, item => item.Pattern == "§");
    }

    [Fact]
    public async Task ExpiredPreparedAttemptIsCleanedAndMarkedExpired()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        context.Time.Advance(TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(1)));
        await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        var attempt = await db.TypingAttempts.SingleAsync(item => item.Id == session.Id);

        Assert.Equal(AttemptPhase.Expired, attempt.Phase);
        Assert.Null(attempt.FinishedAt);
    }

    [Fact]
    public async Task DatabaseStartupAbortsOnlyOrphanedNonterminalAttempts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        Guid preparedId;
        Guid startedId;
        Guid finishedId;
        Guid challengeId;
        Guid profileId;
        await using (var setupDb = new KeyWarsDbContext(options))
        {
            await setupDb.Database.MigrateAsync();
            var profile = new UserProfile
            {
                DisplayName = "Neustart Test",
                SamAccountName = "restart",
                DirectoryObjectGuid = Guid.NewGuid().ToString(),
                DirectorySid = "S-4"
            };
            setupDb.UserProfiles.Add(profile);
            var prepared = Orphan(profile.Id, AttemptPhase.Prepared);
            var started = Orphan(profile.Id, AttemptPhase.Started);
            var finished = Orphan(profile.Id, AttemptPhase.Finished);
            finished.FinishedAt = DateTimeOffset.Parse("2026-06-18T12:01:00Z");
            var text = new TrainingText
            {
                OwnerProfileId = profile.Id,
                Title = "Neustart-Challenge",
                Body = "Neustart",
                Visibility = TrainingTextVisibility.Organization,
                CharacterCount = TypingEngine.SplitGraphemes("Neustart").Count
            };
            var challenge = new Challenge
            {
                CreatorProfileId = profile.Id,
                TrainingTextId = text.Id,
                Title = "Neustart-Challenge",
                Status = ChallengeStatus.Running,
                ExpiresAt = DateTimeOffset.Parse("2026-06-20T12:00:00Z")
            };
            var round = new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1 };
            setupDb.TypingAttempts.AddRange(prepared, started, finished);
            setupDb.TrainingTexts.Add(text);
            setupDb.Challenges.Add(challenge);
            setupDb.ChallengeRounds.Add(round);
            setupDb.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = profile.Id,
                Status = ParticipantStatus.Running
            });
            setupDb.ChallengeAttemptBindings.Add(new ChallengeAttemptBinding
            {
                ChallengeId = challenge.Id,
                ChallengeRoundId = round.Id,
                UserProfileId = profile.Id,
                TypingAttemptId = started.Id,
                TextSnapshotHash = "sha256:test",
                Mode = TrainingMode.Text,
                BindingToken = "restart-binding"
            });
            await setupDb.SaveChangesAsync();
            preparedId = prepared.Id;
            startedId = started.Id;
            finishedId = finished.Id;
            challengeId = challenge.Id;
            profileId = profile.Id;
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new KeyWarsDbContext(options));
        await using var provider = services.BuildServiceProvider();
        var initializer = new DatabaseInitializer(
            provider,
            NullLogger<DatabaseInitializer>.Instance,
            new TestEnvironment("Development"));

        await initializer.InitializeAsync();

        await using var verificationDb = new KeyWarsDbContext(options);
        Assert.Equal(AttemptPhase.Aborted, (await verificationDb.TypingAttempts.FindAsync(preparedId))?.Phase);
        Assert.Equal(AttemptPhase.Aborted, (await verificationDb.TypingAttempts.FindAsync(startedId))?.Phase);
        Assert.Equal(AttemptPhase.Finished, (await verificationDb.TypingAttempts.FindAsync(finishedId))?.Phase);
        Assert.Empty(await verificationDb.ChallengeAttemptBindings.Where(binding => binding.ChallengeId == challengeId).ToListAsync());
        Assert.Equal(
            ParticipantStatus.Joined,
            await verificationDb.ChallengeParticipants
                .Where(participant => participant.ChallengeId == challengeId && participant.UserProfileId == profileId)
                .Select(participant => participant.Status)
                .SingleAsync());
    }

    [Fact]
    public void ConfigurationAliasesBindComposeStyleEnvironmentNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:LDAP:BASE_DN"] = "DC=example,DC=local",
                ["KEYWARS:LDAP:UPN_SUFFIX"] = "example.local",
                ["KEYWARS:LDAP:ALLOW_STARTTLS"] = "true",
                ["KEYWARS:AUTH:COOKIE_LIFETIME_HOURS"] = "6",
                ["KEYWARS:LIVE:MAX_PARTICIPANTS_PER_ROOM"] = "12",
                ["KEYWARS:LIVE:COUNTDOWN_SECONDS"] = "4",
                ["KEYWARS:LIVE:COMPLETION_QUEUE_CAPACITY"] = "32",
                ["KEYWARS:LIVE:COMPLETION_DRAIN_TIMEOUT_SECONDS"] = "25",
                ["KEYWARS:LIVE:MAX_ARENA_TARGET_GRAPHEMES"] = "1200",
                ["KEYWARS:CONTENT:MAX_UPLOAD_BYTES"] = "4096",
                ["KEYWARS:CONTENT:MAX_TEXT_CHARACTERS"] = "2048",
                ["KEYWARS:CONTENT:MAX_TEXT_GRAPHEMES"] = "2040",
                ["KEYWARS:CONTENT:MAX_TEXT_LINES"] = "80",
                ["KEYWARS:RETENTION:ENABLED"] = "true",
                ["KEYWARS:RETENTION:DRY_RUN"] = "false",
                ["KEYWARS:RETENTION:BATCH_SIZE"] = "75",
                ["KEYWARS:RETENTION:BACKUP_RETENTION_DAYS"] = "45"
            })
            .Build();

        var ldap = new LdapOptions();
        ConfigurationAliases.BindLdap(configuration, ldap);
        var auth = new AuthOptions();
        ConfigurationAliases.BindAuth(configuration, auth);
        var live = new LiveOptions();
        ConfigurationAliases.BindLive(configuration, live);
        var content = new ContentOptions();
        ConfigurationAliases.BindContent(configuration, content);
        var retention = new RetentionOptions();
        ConfigurationAliases.BindRetention(configuration, retention);

        Assert.Equal("DC=example,DC=local", ldap.BaseDn);
        Assert.Equal("example.local", ldap.UpnSuffix);
        Assert.True(ldap.AllowStartTls);
        Assert.Equal(6, auth.CookieLifetimeHours);
        Assert.Equal(12, live.MaxParticipantsPerRoom);
        Assert.Equal(4, live.CountdownSeconds);
        Assert.Equal(32, live.CompletionQueueCapacity);
        Assert.Equal(25, live.CompletionDrainTimeoutSeconds);
        Assert.Equal(1200, live.MaxArenaTargetGraphemes);
        Assert.Equal(4096, content.MaxUploadBytes);
        Assert.Equal(2048, content.MaxTextCharacters);
        Assert.Equal(2040, content.MaxTextGraphemes);
        Assert.Equal(80, content.MaxTextLines);
        Assert.True(retention.Enabled);
        Assert.False(retention.DryRun);
        Assert.Equal(75, retention.BatchSize);
        Assert.Equal(45, retention.BackupRetentionDays);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2801")]
    [InlineData("ungültig")]
    public void ArenaTargetConfigurationRejectsUnsafeValues(string configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:LIVE:MAX_ARENA_TARGET_GRAPHEMES"] = configuredValue
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationAliases.BindLive(configuration, new LiveOptions()));

        Assert.Contains("zwischen 1 und 2800", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BATCH_SIZE", "0")]
    [InlineData("BATCH_SIZE", "ungültig")]
    [InlineData("DRY_RUN", "vielleicht")]
    public void RetentionConfigurationRejectsUnsafeValues(string key, string configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"KEYWARS:RETENTION:{key}"] = configuredValue
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ConfigurationAliases.GetRetention(configuration));
    }

    [Fact]
    public void StartupValidationBlocksDevelopmentLoginOutsideDevelopment()
    {
        var configuration = StartupConfiguration("Staging", new Dictionary<string, string?>
        {
            ["KEYWARS:AUTH:DEVELOPMENT_LOGIN"] = "true",
            ["KEYWARS:LDAP:URLS"] = "ldaps://dc01.example.local:636",
            ["KEYWARS:LDAP:BASE_DN"] = "DC=example,DC=local",
            ["KEYWARS:LDAP:UPN_SUFFIX"] = "example.local"
        });

        Assert.Throws<InvalidOperationException>(() =>
            StartupValidator.Validate(configuration, new TestEnvironment("Staging"), NullLogger.Instance));
    }

    [Fact]
    public void StartupValidationAcceptsLdapsOutsideDevelopment()
    {
        var configuration = StartupConfiguration("Staging", new Dictionary<string, string?>
        {
            ["KEYWARS:LDAP:URLS"] = "ldaps://dc01.example.local:636",
            ["KEYWARS:LDAP:BASE_DN"] = "DC=example,DC=local",
            ["KEYWARS:LDAP:UPN_SUFFIX"] = "example.local"
        });

        StartupValidator.Validate(configuration, new TestEnvironment("Staging"), NullLogger.Instance);
    }

    [Fact]
    public void StartupValidationRejectsMissingLdapCaCertificate()
    {
        var configuration = StartupConfiguration("Staging", new Dictionary<string, string?>
        {
            ["KEYWARS:LDAP:URLS"] = "ldaps://dc01.example.local:636",
            ["KEYWARS:LDAP:BASE_DN"] = "DC=example,DC=local",
            ["KEYWARS:LDAP:UPN_SUFFIX"] = "example.local",
            ["KEYWARS:LDAP:CA_CERTIFICATE_PATH"] = Path.Combine(Path.GetTempPath(), $"missing-ca-{Guid.NewGuid():N}.pem")
        });

        Assert.Throws<InvalidOperationException>(() =>
            StartupValidator.Validate(configuration, new TestEnvironment("Staging"), NullLogger.Instance));
    }

    [Fact]
    public void StartupValidationRejectsInvalidLdapCaCertificate()
    {
        var invalidCertificatePath = Path.Combine(
            Path.GetTempPath(),
            $"invalid-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(invalidCertificatePath, "not a certificate");
        try
        {
            var configuration = StartupConfiguration("Staging", new Dictionary<string, string?>
            {
                ["KEYWARS:LDAP:URLS"] = "ldaps://dc01.example.local:636",
                ["KEYWARS:LDAP:BASE_DN"] = "DC=example,DC=local",
                ["KEYWARS:LDAP:UPN_SUFFIX"] = "example.local",
                ["KEYWARS:LDAP:CA_CERTIFICATE_PATH"] = invalidCertificatePath
            });

            var exception = Assert.Throws<InvalidOperationException>(() =>
                StartupValidator.Validate(configuration, new TestEnvironment("Staging"), NullLogger.Instance));

            Assert.Contains("X.509-Zertifikat", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(invalidCertificatePath);
        }
    }

    [Fact]
    public void StartupValidationRejectsInvalidLdapTimeout()
    {
        var configuration = StartupConfiguration("Staging", new Dictionary<string, string?>
        {
            ["KEYWARS:LDAP:URLS"] = "ldaps://dc01.example.local:636",
            ["KEYWARS:LDAP:BASE_DN"] = "DC=example,DC=local",
            ["KEYWARS:LDAP:UPN_SUFFIX"] = "example.local",
            ["KEYWARS:LDAP:CONNECT_TIMEOUT_SECONDS"] = "0"
        });

        Assert.Throws<InvalidOperationException>(() =>
            StartupValidator.Validate(configuration, new TestEnvironment("Staging"), NullLogger.Instance));
    }

    private static IConfiguration StartupConfiguration(string environment, Dictionary<string, string?> values)
    {
        values["KEYWARS:DATA:DIRECTORY"] = Path.Combine(Path.GetTempPath(), $"keywars-startup-{environment}-{Guid.NewGuid():N}");
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static TypingAttempt Orphan(Guid profileId, AttemptPhase phase)
    {
        var preparedAt = DateTimeOffset.Parse("2026-06-18T12:00:00Z");
        return new TypingAttempt
        {
            UserProfileId = profileId,
            Mode = TrainingMode.Words10,
            Phase = phase,
            Nonce = Guid.NewGuid().ToString("N")[..24],
            TextHash = "sha256:test",
            PreparedAt = preparedAt,
            StartedAt = preparedAt
        };
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "KeyWars.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AttemptTestContext : IAsyncDisposable
    {
        private AttemptTestContext(SqliteConnection connection, DbContextOptions<KeyWarsDbContext> options, ManualTimeProvider time, Guid profileId)
        {
            Connection = connection;
            Options = options;
            Time = time;
            ProfileId = profileId;
        }

        public SqliteConnection Connection { get; }
        public DbContextOptions<KeyWarsDbContext> Options { get; }
        public AttemptSessionStore SessionStore { get; } = new();
        public ManualTimeProvider Time { get; }
        public Guid ProfileId { get; }

        public static async Task<AttemptTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
            await using var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var profile = new UserProfile
            {
                DisplayName = "Carla Test",
                SamAccountName = "ctest",
                DirectoryObjectGuid = Guid.NewGuid().ToString(),
                DirectorySid = "S-3"
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
            return new AttemptTestContext(connection, options, time, profile.Id);
        }

        public AttemptService CreateService(KeyWarsDbContext db) =>
            new(db, new TypingEngine(Time), new MotivationService(db, Time), Time, SessionStore);

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan value) => utcNow += value;
    }
}
