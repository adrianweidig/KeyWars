using System.Net;
using System.Text.RegularExpressions;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyWars.E2ETests;

public sealed partial class WebSmokeTests : IClassFixture<KeyWarsWebFactory>
{
    private readonly KeyWarsWebFactory factory;

    public WebSmokeTests(KeyWarsWebFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task DevelopmentUserCanLoginAndOpenDashboard()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await LoginAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        var text = new TrainingText
        {
            OwnerProfileId = profile.Id,
            Title = "Dashboard Text",
            Body = "Dashboard Text",
            CharacterCount = 14,
            Visibility = TrainingTextVisibility.Private
        };
        db.TrainingTexts.Add(text);
        var challenge = new Challenge
        {
            CreatorProfileId = profile.Id,
            TrainingTextId = text.Id,
            Title = "Team Sprint",
            Status = ChallengeStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        db.Challenges.Add(challenge);
        db.ChallengeParticipants.Add(new ChallengeParticipant
        {
            ChallengeId = challenge.Id,
            UserProfileId = profile.Id,
            Status = ParticipantStatus.Joined,
            InvitedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var dashboard = await client.GetStringAsync("/");
        Assert.Contains("Max Mustermann", dashboard);
        Assert.Contains("Sofort tippen", dashboard);
        Assert.Contains("Tagesfokus", dashboard);
        Assert.Contains("30-Tage-Übersicht", dashboard);
        Assert.Contains("Team Sprint", dashboard);
        Assert.Contains("Offen", dashboard);
        Assert.DoesNotContain(">Open<", dashboard);
    }

    [Fact]
    public async Task LegacyArenaRaceRouteRedirectsToCanonicalRoomWithoutManualFinishFallback()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        var rooms = scope.ServiceProvider.GetRequiredService<LiveRoomManager>();
        var room = rooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Smoke", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        var legacy = await client.GetAsync($"/arena/{room.RoomId}/rennen");
        var canonical = await client.GetStringAsync($"/arena/{room.RoomId}");

        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        Assert.Equal($"/arena/{room.RoomId}", legacy.Headers.Location?.ToString());
        Assert.DoesNotContain("Zieleinlauf speichern", canonical);
        Assert.DoesNotContain("data-arena-finish-form", canonical);
        Assert.Contains("Runde aufgeben", canonical);
        Assert.Contains("data-arena-track", canonical);
        Assert.Contains("data-arena-hud", canonical);
        Assert.Contains("data-arena-podium", canonical);
        Assert.Contains("aria-live=\"polite\"", canonical);
        Assert.Contains("25", canonical);
        Assert.Contains("Ziel", canonical);
    }

    [Fact]
    public async Task ProfilePageRendersAggregatedInsightsWithoutRawEnums()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        db.TypingAttempts.Add(new TypingAttempt
        {
            UserProfileId = profile.Id,
            Mode = TrainingMode.Sprint60,
            Phase = AttemptPhase.Finished,
            Completed = true,
            Official = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PreparedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            DurationMilliseconds = 60_000,
            CorrectCharacters = 240,
            TotalCharacters = 240,
            Wpm = 48,
            RawWpm = 48,
            Accuracy = 100,
            Consistency = 92,
            ConsistencySampleCount = 8
        });
        await db.SaveChangesAsync();

        var profilePage = await client.GetStringAsync("/profil");

        Assert.Contains("Gesamtleistung", profilePage);
        Assert.Contains("Trendwerte als Tabelle", profilePage);
        Assert.Contains("Aktivitätskalender", profilePage);
        Assert.Contains("Bestwerte je Modus", profilePage);
        Assert.Contains("60-Sekunden-Sprint", profilePage);
        Assert.DoesNotContain("Sprint60", profilePage);
        Assert.Contains("<svg", profilePage);
    }

    [Fact]
    public async Task ArenaLobbyRendersEntryPathsAndRoomCapacityWithoutInfrastructureCopy()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        var rooms = scope.ServiceProvider.GetRequiredService<LiveRoomManager>();
        rooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Offene Runde", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        var arena = await client.GetStringAsync("/arena");

        Assert.Contains("Raum erstellen", arena);
        Assert.Contains("Code eingeben", arena);
        Assert.Contains("Offene Runde", arena);
        Assert.Contains("Max Mustermann", arena);
        Assert.Contains("1 / 8", arena);
        Assert.Contains("Klassisches Rennen", arena);
        Assert.DoesNotContain("Arbeitsspeicher", arena);
        Assert.DoesNotContain("Neustart", arena);
    }

    [Fact]
    public async Task ArenaCreateFormUsesConfiguredParticipantLimit()
    {
        using var customFactory = new ConfiguredKeyWarsWebFactory(12);
        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var form = await client.GetStringAsync("/arena/neu");

        Assert.Contains("2 bis 12 Personen", form);
        Assert.Contains("max=\"12\"", form);
        Assert.DoesNotContain("max=\"64\"", form);
        Assert.DoesNotContain("Einladungen", form);
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/anmelden");
        var token = AntiForgeryRegex().Match(login).Groups["token"].Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = "max.mustermann",
            ["Input.Password"] = "lokales-test-passwort",
            ["__RequestVerificationToken"] = token
        });

        return await client.PostAsync("/anmelden", form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")]
    private static partial Regex AntiForgeryRegex();
}

public sealed class KeyWarsWebFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"keywars-e2e-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("KEYWARS:DATA:DIRECTORY", dataDirectory);
        builder.UseSetting("KEYWARS:AUTH:DEVELOPMENT_LOGIN", "true");
    }
}

public sealed class ConfiguredKeyWarsWebFactory(int maxParticipantsPerRoom) : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"keywars-e2e-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("KEYWARS:DATA:DIRECTORY", dataDirectory);
        builder.UseSetting("KEYWARS:AUTH:DEVELOPMENT_LOGIN", "true");
        builder.UseSetting("KEYWARS:LIVE:MAX_PARTICIPANTS_PER_ROOM", maxParticipantsPerRoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
