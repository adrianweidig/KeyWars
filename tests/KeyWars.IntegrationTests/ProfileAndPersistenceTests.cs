using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        var sessionStore = new AttemptSessionStore();
        var engine = new TypingEngine(TimeProvider.System);

        Guid profileId;
        AttemptSession session;
        await using (var db = new KeyWarsDbContext(options))
        {
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
            profileId = profile.Id;

            var starter = new AttemptService(db, engine, new MotivationService(db, TimeProvider.System), TimeProvider.System, sessionStore);
            session = await starter.StartAsync(profileId, new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        }

        await using (var db = new KeyWarsDbContext(options))
        {
            var finisher = new AttemptService(db, engine, new MotivationService(db, TimeProvider.System), TimeProvider.System, sessionStore);
            var attempt = await finisher.FinishAsync(profileId, new FinishAttemptRequest(session.Id, session.Text, 0, 0, 5000));

            Assert.True(attempt.Completed);
            Assert.Equal(session.Id, attempt.Id);
            Assert.NotNull(attempt.FinishedAt);
        }
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
        Assert.Equal(4096, content.MaxUploadBytes);
    }
}
