using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    public async Task InvalidNonceDoesNotConsumeActiveSession()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(4));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FinishAsync(
                context.ProfileId,
                new FinishAttemptRequest(session.Id, session.Text, 0, 0, 4000) { Nonce = "bad-nonce" }));

        var attempt = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 4000) { Nonce = session.Nonce });

        Assert.True(attempt.Completed);
        Assert.Equal(AttemptPhase.Finished, attempt.Phase);
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

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Sprint60, null, 60, 80));
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
    public async Task FinishPersistsWordTimingAndActualErrorPatternsOnly()
    {
        await using var context = await AttemptTestContext.CreateAsync();
        await using var db = new KeyWarsDbContext(context.Options);
        const string textBody = "eins zwei drei";
        var text = new TrainingText
        {
            OwnerProfileId = context.ProfileId,
            Title = "Test",
            Body = textBody,
            CharacterCount = TypingEngine.SplitGraphemes(textBody).Count
        };
        db.TrainingTexts.Add(text);
        await db.SaveChangesAsync();
        var service = context.CreateService(db);

        var session = await service.StartAsync(context.ProfileId, new StartAttemptRequest(TrainingMode.Sprint60, text.Id, 60, null));
        await service.BeginAsync(context.ProfileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        var attempt = await service.FinishAsync(
            context.ProfileId,
            new FinishAttemptRequest(session.Id, "eins xwei drei", 0, 0, 10_000)
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
        Assert.Equal("z", error.Expected);
        Assert.Equal("x", error.Actual);
        Assert.Contains(observations, item => item.Pattern == "z" && item.Errors == 1);
        Assert.Contains(observations, item => item.Pattern == "zw" && item.Errors == 1);
        Assert.DoesNotContain(observations, item => item.Pattern == "dr");
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
                ["KEYWARS:CONTENT:MAX_UPLOAD_BYTES"] = "4096"
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

        Assert.Equal("DC=example,DC=local", ldap.BaseDn);
        Assert.Equal("example.local", ldap.UpnSuffix);
        Assert.True(ldap.AllowStartTls);
        Assert.Equal(6, auth.CookieLifetimeHours);
        Assert.Equal(12, live.MaxParticipantsPerRoom);
        Assert.Equal(4, live.CountdownSeconds);
        Assert.Equal(32, live.CompletionQueueCapacity);
        Assert.Equal(4096, content.MaxUploadBytes);
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
