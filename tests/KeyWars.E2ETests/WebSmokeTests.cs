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
        Assert.DoesNotContain("style=", dashboard);
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
        Assert.DoesNotContain("style=", canonical);
    }

    [Fact]
    public async Task ArenaRoomUsesFocusedWindowForLargeParticipantFields()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        var rooms = scope.ServiceProvider.GetRequiredService<LiveRoomManager>();
        var room = rooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Große Runde", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 64));
        foreach (var index in Enumerable.Range(1, 31))
        {
            rooms.Join(room.RoomId, Guid.CreateVersion7(), $"Alpha {index:00}");
        }

        var page = await client.GetStringAsync($"/arena/{room.RoomId}");
        var decodedPage = WebUtility.HtmlDecode(page);

        Assert.Contains("data-arena-display-mode=\"focused\"", page);
        Assert.Contains("Fokussierte Ansicht", decodedPage);
        Assert.Contains("5 von 32 Teilnehmenden im Fokus", decodedPage);
        Assert.Contains("Kapazität 64", decodedPage);
        Assert.Contains("Zuschauerrolle vorbereitet", decodedPage);
        Assert.Contains("Max Mustermann", decodedPage);
        Assert.Contains("Alpha 01", decodedPage);
        Assert.Contains("Alpha 03", decodedPage);
        Assert.Contains("Alpha 31", decodedPage);
        Assert.Contains("27 weitere Teilnehmende", decodedPage);
        Assert.DoesNotContain("Alpha 04", decodedPage);
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
        Assert.DoesNotContain("style=", profilePage);
        Assert.Contains("<svg", profilePage);
    }

    [Fact]
    public async Task ProfileSettingsPersistArenaFeedbackPreferences()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var settingsResponse = await client.GetAsync("/profil/einstellungen");
        var settings = await settingsResponse.Content.ReadAsStringAsync();
        var token = AntiForgeryRegex().Match(settings).Groups["token"].Value;
        var response = await client.PostAsync("/profil/einstellungen", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Motto"] = "Feedback testen",
            ["Input.PreferredMode"] = TrainingMode.Sprint60.ToString(),
            ["Input.LeaderboardVisible"] = "true",
            ["Input.GhostSharingEnabled"] = "false",
            ["Input.ShowLiveWpm"] = "false",
            ["Input.ShowLiveRankChanges"] = "false",
            ["Input.SoundEnabled"] = "true",
            ["Input.SoundVolumePercent"] = "70",
            ["Input.ReactionsEnabled"] = "false",
            ["Input.ReducedMotion"] = "true",
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("de-DE", settingsResponse.Content.Headers.ContentLanguage.Single());
        Assert.Equal("/profil/einstellungen", response.Headers.Location?.ToString());
        await using var scope = isolatedFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        Assert.False(profile.ShowLiveWpm);
        Assert.False(profile.ShowLiveRankChanges);
        Assert.True(profile.SoundEnabled);
        Assert.Equal(70, profile.SoundVolumePercent);
        Assert.False(profile.ReactionsEnabled);
        Assert.True(profile.ReducedMotion);

        var savedSettings = WebUtility.HtmlDecode(await client.GetStringAsync("/profil/einstellungen"));
        Assert.Contains("Einstellungen gespeichert.", savedSettings);
        Assert.Contains("Identität aus AD/LDAP", savedSettings);
        Assert.Contains("max.mustermann", savedSettings);
        Assert.Contains("Darstellung", savedSettings);
        Assert.Contains("Training", savedSettings);
        Assert.Contains("Arena", savedSettings);
        Assert.Contains("Profil und Privatsphäre", savedSettings);

        var rooms = scope.ServiceProvider.GetRequiredService<LiveRoomManager>();
        var room = rooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Feedback Runde", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        var page = await client.GetStringAsync($"/arena/{room.RoomId}");
        var decodedPage = WebUtility.HtmlDecode(page);

        Assert.Contains("data-sound-enabled=\"true\"", page);
        Assert.Contains("data-sound-volume=\"70\"", page);
        Assert.Contains("data-reduced-motion=\"true\"", page);
        Assert.Contains("data-reactions-enabled=\"false\"", page);
        Assert.DoesNotContain("data-hud-wpm", page);
        Assert.DoesNotContain("Positive Arena-Reaktionen", decodedPage);
    }

    [Fact]
    public async Task PrivacyActionsRequireCurrentAccountConfirmation()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var seedScope = isolatedFactory.Services.CreateAsyncScope();
        var db = seedScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        profile.ExperiencePoints = 250;
        profile.Level = 3;
        profile.ArenaRating = 1180;
        await db.SaveChangesAsync();

        var resetPage = await client.GetStringAsync("/profil/statistik-zuruecksetzen");
        Assert.Contains("max.mustermann", WebUtility.HtmlDecode(resetPage));
        var resetToken = AntiForgeryRegex().Match(resetPage).Groups["token"].Value;
        var rejectedReset = await client.PostAsync("/profil/statistik-zuruecksetzen", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Confirmation"] = "falsch",
            ["__RequestVerificationToken"] = resetToken
        }));

        Assert.Equal(HttpStatusCode.OK, rejectedReset.StatusCode);
        var rejectedResetBody = WebUtility.HtmlDecode(await rejectedReset.Content.ReadAsStringAsync());
        Assert.Contains("Gib max.mustermann ein", rejectedResetBody);
        await db.Entry(profile).ReloadAsync();
        Assert.Equal(250, profile.ExperiencePoints);
        Assert.Equal(3, profile.Level);
        Assert.Equal(1180, profile.ArenaRating);

        var acceptedReset = await client.PostAsync("/profil/statistik-zuruecksetzen", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Confirmation"] = "max.mustermann",
            ["__RequestVerificationToken"] = resetToken
        }));

        Assert.Equal(HttpStatusCode.Redirect, acceptedReset.StatusCode);
        Assert.Equal("/profil", acceptedReset.Headers.Location?.ToString());
        await db.Entry(profile).ReloadAsync();
        Assert.Equal(0, profile.ExperiencePoints);
        Assert.Equal(1, profile.Level);
        Assert.Equal(1000, profile.ArenaRating);

        var deletePage = await client.GetStringAsync("/profil/loeschen");
        Assert.Contains("max.mustermann", WebUtility.HtmlDecode(deletePage));
        var deleteToken = AntiForgeryRegex().Match(deletePage).Groups["token"].Value;
        var rejectedDelete = await client.PostAsync("/profil/loeschen", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Confirmation"] = "falsch",
            ["__RequestVerificationToken"] = deleteToken
        }));

        Assert.Equal(HttpStatusCode.OK, rejectedDelete.StatusCode);
        var rejectedDeleteBody = WebUtility.HtmlDecode(await rejectedDelete.Content.ReadAsStringAsync());
        Assert.Contains("Gib max.mustermann ein", rejectedDeleteBody);
        await db.Entry(profile).ReloadAsync();
        Assert.False(profile.Deleted);

        var acceptedDelete = await client.PostAsync("/profil/loeschen", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Confirmation"] = "max.mustermann",
            ["__RequestVerificationToken"] = deleteToken
        }));

        Assert.Equal(HttpStatusCode.Redirect, acceptedDelete.StatusCode);
        Assert.Equal("/anmelden", acceptedDelete.Headers.Location?.ToString());
        await db.Entry(profile).ReloadAsync();
        Assert.True(profile.Deleted);
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
