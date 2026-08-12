using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Pages;
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
    public async Task HttpsResponsesAndCookiesUseHardenedHeaderContract()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var health = await client.GetAsync("/health/live");
        var login = await LoginAsync(client);

        Assert.Equal("max-age=31536000; includeSubDomains", health.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Contains("form-action 'self'", health.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", health.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        var authCookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("KeyWars.Dev.Auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArenaPersistenceStatusPrefersSummaryAndReturnsOnlyState()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        var roomId = Guid.CreateVersion7();
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profileId = await db.UserProfiles
                .Where(profile => profile.SamAccountName == "max.mustermann" && !profile.Deleted)
                .Select(profile => profile.Id)
                .SingleAsync();
            db.LiveRoomSummaries.Add(new LiveRoomSummary
            {
                Id = roomId,
                IdempotencyKey = $"{roomId:N}:1:1",
                CreatorProfileId = profileId,
                RoomCode = "ABC234",
                Mode = LiveRoomMode.Classic,
                Visibility = LiveRoomVisibility.InternalOpen,
                RoundCount = 1,
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
                FinishedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var persistedResponse = await client.GetAsync($"/api/arena/{roomId}/speicherstatus");
        using var persisted = JsonDocument.Parse(await persistedResponse.Content.ReadAsStringAsync());
        var unknownResponse = await client.GetAsync($"/api/arena/{Guid.CreateVersion7()}/speicherstatus");
        using var unknown = JsonDocument.Parse(await unknownResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, persistedResponse.StatusCode);
        Assert.Equal(["state"], persisted.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(nameof(CompletionState.Persisted), persisted.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, unknownResponse.StatusCode);
        Assert.Equal(["state"], unknown.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(nameof(CompletionState.AbortedUnconfirmed), unknown.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task EarlyPartialSprintReturnsTypedProblemWithoutMutation()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var startResponse = await client.PostAsJsonAsync("/api/spielen/start", new
        {
            mode = "Sprint60",
            sprintSeconds = 15,
            wordCount = 120
        });
        startResponse.EnsureSuccessStatusCode();
        using var start = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var attemptId = start.RootElement.GetProperty("id").GetGuid();
        var nonce = start.RootElement.GetProperty("nonce").GetString();
        var text = start.RootElement.GetProperty("text").GetString()!;

        var beginResponse = await client.PostAsJsonAsync("/api/spielen/begin", new { attemptId, nonce });
        beginResponse.EnsureSuccessStatusCode();
        using var begin = JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync());
        Assert.Equal(60, Math.Round((begin.RootElement.GetProperty("endsAt").GetDateTimeOffset() - begin.RootElement.GetProperty("startedAt").GetDateTimeOffset()).TotalSeconds));
        Assert.True(begin.RootElement.TryGetProperty("serverNow", out _));

        var finishResponse = await client.PostAsJsonAsync("/api/spielen/abschliessen", new
        {
            attemptId,
            nonce,
            input = text[..Math.Min(10, text.Length)],
            backspaces = 0,
            focusLosses = 0,
            clientDurationMilliseconds = 10
        });
        using var problem = JsonDocument.Parse(await finishResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, finishResponse.StatusCode);
        Assert.Equal("application/problem+json", finishResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AttemptErrorCodes.StillRunning, problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("retryAfterMs").GetInt32() > 0);
    }

    [Fact]
    public async Task ExactWordAttemptReportsTargetCompletionAndConsistencyEvidence()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var startResponse = await client.PostAsJsonAsync("/api/spielen/start", new
        {
            mode = "Words10",
            sprintSeconds = 0,
            wordCount = 10
        });
        startResponse.EnsureSuccessStatusCode();
        using var start = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var attemptId = start.RootElement.GetProperty("id").GetGuid();
        var nonce = start.RootElement.GetProperty("nonce").GetString();
        var text = start.RootElement.GetProperty("text").GetString()!;
        (await client.PostAsJsonAsync("/api/spielen/begin", new { attemptId, nonce })).EnsureSuccessStatusCode();

        var finishResponse = await client.PostAsJsonAsync("/api/spielen/abschliessen", new
        {
            attemptId,
            nonce,
            input = text,
            backspaces = 0,
            focusLosses = 0,
            clientDurationMilliseconds = 3_000,
            wordDurationsMilliseconds = new[] { 900, 1_000, 1_100 }
        });
        finishResponse.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await finishResponse.Content.ReadAsStringAsync());

        Assert.True(result.RootElement.GetProperty("completed").GetBoolean());
        Assert.True(result.RootElement.GetProperty("targetCompleted").GetBoolean());
        Assert.Equal(3, result.RootElement.GetProperty("consistencySampleCount").GetInt32());
        Assert.True(result.RootElement.GetProperty("durationMilliseconds").GetInt32() >= 1_000);
        Assert.Equal(0, result.RootElement.GetProperty("incorrectCharacters").GetInt32());
    }

    [Fact]
    public async Task IncompatibleAttemptTargetReturnsTypedBadRequest()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/spielen/start", new
        {
            mode = "Words100",
            sprintSeconds = 0,
            wordCount = 10
        });
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AttemptErrorCodes.InvalidRequest, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ManipulatedAttemptStartRejectsOwnedQuarantinedText()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        Guid textId;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
            var text = new TrainingText
            {
                OwnerProfileId = profile.Id,
                Title = "Quarantänisierter Eigentext",
                Body = "Dieser Text darf trotz manipulierter Auswahl nicht gestartet werden.",
                Visibility = TrainingTextVisibility.Private,
                IsQuarantined = true
            };
            db.TrainingTexts.Add(text);
            await db.SaveChangesAsync();
            textId = text.Id;
        }

        var response = await client.PostAsJsonAsync("/api/spielen/start", new
        {
            mode = "Text",
            trainingTextId = textId
        });
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AttemptErrorCodes.InvalidRequest, problem.RootElement.GetProperty("code").GetString());
        await using var verificationScope = isolatedFactory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.False(await verificationDb.TypingAttempts.AnyAsync(item => item.TrainingTextId == textId));
    }

    [Fact]
    public async Task ChallengeConflictReturnsTypedProblemDetails()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        Guid challengeId;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var invitee = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
            var creator = new UserProfile
            {
                DisplayName = "Challenge Creator",
                SamAccountName = "challenge.creator",
                DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
                DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}"
            };
            var text = new TrainingText
            {
                OwnerProfileId = creator.Id,
                Title = "API Challenge",
                Body = "API Challenge",
                Visibility = TrainingTextVisibility.Organization,
                CharacterCount = TypingEngine.SplitGraphemes("API Challenge").Count
            };
            var challenge = new Challenge
            {
                CreatorProfileId = creator.Id,
                TrainingTextId = text.Id,
                Title = "API Challenge",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            db.UserProfiles.Add(creator);
            db.TrainingTexts.Add(text);
            db.Challenges.Add(challenge);
            db.ChallengeRounds.Add(new ChallengeRound { ChallengeId = challenge.Id });
            db.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = creator.Id,
                Status = ParticipantStatus.Joined
            });
            db.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = invitee.Id,
                Status = ParticipantStatus.Invited
            });
            await db.SaveChangesAsync();
            challengeId = challenge.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/herausforderungen/{challengeId}/start", new { });
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ChallengeErrorCodes.Conflict, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExpiredChallengeDeclineReturnsGoneWithInlineMessage()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        Guid challengeId;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
            var text = new TrainingText
            {
                OwnerProfileId = profile.Id,
                Title = "Expired Challenge",
                Body = "Expired Challenge",
                Visibility = TrainingTextVisibility.Private,
                CharacterCount = TypingEngine.SplitGraphemes("Expired Challenge").Count
            };
            var challenge = new Challenge
            {
                CreatorProfileId = profile.Id,
                TrainingTextId = text.Id,
                Title = "Expired Challenge",
                Status = ChallengeStatus.Open,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            db.TrainingTexts.Add(text);
            db.Challenges.Add(challenge);
            db.ChallengeRounds.Add(new ChallengeRound { ChallengeId = challenge.Id });
            db.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = profile.Id,
                Status = ParticipantStatus.Joined
            });
            await db.SaveChangesAsync();
            challengeId = challenge.Id;
        }

        var details = await client.GetStringAsync($"/herausforderungen/{challengeId}");
        var token = AntiForgeryRegex().Match(details).Groups["token"].Value;
        var response = await client.PostAsync(
            $"/herausforderungen/{challengeId}?handler=Decline",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("Diese Herausforderung ist abgelaufen.", body);
        Assert.DoesNotContain("Ein unerwarteter Fehler", body);
        Assert.DoesNotContain(">Ablehnen<", body);
        Assert.DoesNotContain(">Runde spielen<", body);

        var playResponse = await client.GetAsync($"/herausforderungen/{challengeId}/spielen");
        Assert.Equal(HttpStatusCode.Redirect, playResponse.StatusCode);
        Assert.Equal($"/herausforderungen/{challengeId}", playResponse.Headers.Location?.OriginalString);

        var redirectedDetails = await client.GetStringAsync(playResponse.Headers.Location);
        Assert.Contains("Diese Herausforderung ist abgelaufen.", WebUtility.HtmlDecode(redirectedDetails));
    }

    [Fact]
    public async Task InvalidChallengeCreationReturnsBadRequestWithInlineMessage()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        Guid textId;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            textId = await db.TrainingTexts.Select(item => item.Id).FirstAsync();
        }

        var page = await client.GetStringAsync("/herausforderungen/neu");
        var token = AntiForgeryRegex().Match(page).Groups["token"].Value;
        var response = await client.PostAsync(
            "/herausforderungen/neu",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Title"] = "Ungültige Herausforderung",
                ["Input.TrainingTextId"] = textId.ToString(),
                ["Input.Mode"] = nameof(ChallengeMode.Classic),
                ["Input.RoundCount"] = "1",
                ["Input.ExpiryDays"] = "7",
                ["__RequestVerificationToken"] = token
            }));
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Eine Herausforderung benötigt mindestens zwei Personen.", body);
        Assert.DoesNotContain("Ein unerwarteter Fehler", body);
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
        Assert.Contains("Sofortrunde", dashboard);
        Assert.Contains("Tages-Sprint 60s", dashboard);
        Assert.Contains("Deine Quests", dashboard);
        Assert.Contains("Schnell starten", dashboard);
        Assert.Contains("Zum Wettbewerb", dashboard);
        Assert.DoesNotContain(">Open<", dashboard);
        Assert.DoesNotContain("style=", dashboard);
    }

    [Fact]
    public async Task LoginPageCanBeRenderedRepeatedlyWithoutConsumingLoginAttemptLimit()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var index = 0; index < 20; index++)
        {
            var response = await client.GetAsync("/anmelden");
            var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Benutzername", body);
            Assert.Contains("Passwort", body);
        }
    }

    [Fact]
    public async Task LogoutUsesPublicShellAndRedirectsToSignedOutState()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var logoutPage = await client.GetStringAsync("/abmelden");
        var decodedLogout = WebUtility.HtmlDecode(logoutPage);

        Assert.Contains("KeyWars verlassen", decodedLogout);
        Assert.Contains("Jetzt abmelden", decodedLogout);
        Assert.DoesNotContain("status-cockpit", logoutPage);
        Assert.DoesNotContain("desktop-sidebar", logoutPage);
        Assert.DoesNotContain("mobile-bottom-nav", logoutPage);
        Assert.DoesNotContain("Tage Streak", decodedLogout);

        var token = AntiForgeryRegex().Match(logoutPage).Groups["token"].Value;
        var response = await client.PostAsync("/abmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/abmelden?abgemeldet=1", response.Headers.Location?.ToString());

        var signedOutPage = await client.GetStringAsync("/abmelden?abgemeldet=1");
        var decodedSignedOut = WebUtility.HtmlDecode(signedOutPage);

        Assert.Contains("Du bist abgemeldet", decodedSignedOut);
        Assert.Contains("Wieder anmelden", decodedSignedOut);
        Assert.DoesNotContain("status-cockpit", signedOutPage);
        Assert.DoesNotContain("desktop-sidebar", signedOutPage);
        Assert.DoesNotContain("mobile-bottom-nav", signedOutPage);
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
        Assert.Contains("data-copy-text", canonical);
        Assert.Contains("Code kopieren", canonical);
        Assert.Contains("Einladung teilen", canonical);
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
        Guid hiddenProfileId = default;
        foreach (var index in Enumerable.Range(1, 31))
        {
            var participantId = Guid.CreateVersion7();
            rooms.Join(room.RoomId, participantId, $"Alpha {index:00}");
            if (index == 4)
            {
                hiddenProfileId = participantId;
            }
        }

        var page = await client.GetStringAsync($"/arena/{room.RoomId}");
        var decodedPage = WebUtility.HtmlDecode(page);

        Assert.Contains("data-arena-display-mode=\"focused\"", page);
        Assert.Contains("Fokussierte Ansicht", decodedPage);
        Assert.Contains("5 von 32 Teilnehmenden im Fokus", decodedPage);
        Assert.Contains("Kapazität 64", decodedPage);
        Assert.DoesNotContain("Zuschauer", decodedPage);
        Assert.Contains("Max Mustermann", decodedPage);
        Assert.Contains("Alpha 01", decodedPage);
        Assert.Contains("Alpha 03", decodedPage);
        Assert.Contains("Alpha 31", decodedPage);
        Assert.Contains("27 weitere Teilnehmende", decodedPage);
        Assert.DoesNotContain($"data-live-participant-id=\"{hiddenProfileId}\"", page);
        Assert.DoesNotContain($"data-track-participant-id=\"{hiddenProfileId}\"", page);
        Assert.DoesNotContain($"data-participant-id=\"{hiddenProfileId}\"", page);
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
    public async Task DeletedProfileRevokesOtherSessionAndFreshLoginCreatesNewProfile()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var deletingClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var otherSession = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(deletingClient);
        await LoginAsync(otherSession);

        Guid deletedProfileId;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            deletedProfileId = await db.UserProfiles
                .Where(profile => profile.SamAccountName == "max.mustermann" && !profile.Deleted)
                .Select(profile => profile.Id)
                .SingleAsync();
        }

        var deletePage = await deletingClient.GetStringAsync("/profil/loeschen");
        var deleteToken = AntiForgeryRegex().Match(deletePage).Groups["token"].Value;
        var deleted = await deletingClient.PostAsync("/profil/loeschen", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Confirmation"] = "max.mustermann",
            ["__RequestVerificationToken"] = deleteToken
        }));

        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        var rejectedOldSession = await otherSession.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, rejectedOldSession.StatusCode);
        Assert.Equal("/anmelden?ReturnUrl=%2F", rejectedOldSession.Headers.Location?.ToString());

        var relogin = await LoginAsync(otherSession);
        Assert.Equal(HttpStatusCode.Redirect, relogin.StatusCode);
        Assert.Equal("/", relogin.Headers.Location?.ToString());

        await using var verificationScope = isolatedFactory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var deletedProfile = await verificationDb.UserProfiles.SingleAsync(profile => profile.Id == deletedProfileId);
        var replacement = await verificationDb.UserProfiles.SingleAsync(profile =>
            profile.SamAccountName == "max.mustermann" && !profile.Deleted);
        Assert.True(deletedProfile.Deleted);
        Assert.NotEqual(deletedProfileId, replacement.Id);
    }

    [Fact]
    public async Task TemporaryProfileGateRejectsRequestWithoutRevokingCookie()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var scope = isolatedFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profileId = await db.UserProfiles
            .Where(profile => profile.SamAccountName == "max.mustermann" && !profile.Deleted)
            .Select(profile => profile.Id)
            .SingleAsync();
        var gate = scope.ServiceProvider.GetRequiredService<ProfileAccessGate>();
        Assert.True(gate.TryBeginOperation(profileId));

        var blocked = await client.GetAsync("/profil");

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var problem = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("profile_operation_in_progress", problem.RootElement.GetProperty("code").GetString());

        gate.CompleteOperation(profileId);
        var recovered = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ArenaLobbyRendersEntryPathsAndRoomCapacityWithoutInfrastructureCopy()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        await using var scope = isolatedFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        var rooms = scope.ServiceProvider.GetRequiredService<LiveRoomManager>();
        for (var index = 1; index <= 21; index++)
        {
            rooms.CreateRoom(new CreateLiveRoomRequest(
                profile.Id,
                profile.DisplayName,
                $"Offene Runde {index:00}",
                "Text",
                LiveRoomMode.Classic,
                LiveRoomVisibility.InternalOpen,
                1,
                8));
        }

        var arena = await client.GetStringAsync("/arena");
        var secondPage = await client.GetStringAsync("/arena?Seite=2");

        Assert.Contains("Live-Rennen starten", arena);
        Assert.Contains("Code beitreten", arena);
        Assert.Contains("Offene Runde 01", arena);
        Assert.Contains("Offene Runde 20", arena);
        Assert.DoesNotContain("Offene Runde 21", arena);
        Assert.Contains("Seite 1 von 2", arena);
        Assert.Contains("Offene Runde 21", secondPage);
        Assert.DoesNotContain("Offene Runde 01", secondPage);
        Assert.Contains("Seite 2 von 2", secondPage);
        Assert.Contains("Max Mustermann", arena);
        Assert.Contains("1 / 8", arena);
        Assert.Contains("Klassisches Rennen", arena);
        Assert.Contains("Code kopieren", arena);
        Assert.Contains("Einladung teilen", arena);
        Assert.Contains("data-copy-status", arena);
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
        Assert.Contains("data-arena-create-form", form);
        Assert.Contains("data-arena-text-select", form);
        Assert.Contains("data-arena-text-preview", form);
        Assert.Contains("Textvorschau", WebUtility.HtmlDecode(form));
        Assert.Contains("Klassisches Rennen", WebUtility.HtmlDecode(form));
        Assert.Contains("Live-Textboard", WebUtility.HtmlDecode(form));
        Assert.Contains("Serienrennen", WebUtility.HtmlDecode(form));
        Assert.Contains("Teamwertung", WebUtility.HtmlDecode(form));
        Assert.Contains("data-arena-mode-input", form);
        Assert.Contains("data-arena-round-count", form);
        Assert.DoesNotContain("Geplant", WebUtility.HtmlDecode(form));
        Assert.DoesNotContain("aria-disabled=\"true\"", form);
        Assert.Contains("data-submit-guard", form);
        Assert.DoesNotContain("max=\"64\"", form);
        Assert.Contains("Nur eingeladene Personen", WebUtility.HtmlDecode(form));
        Assert.Contains("data-arena-invitations", form);
    }

    [Fact]
    public async Task ArenaCapacityReturnsRetryableServiceUnavailablePage()
    {
        using var customFactory = new ConfiguredKeyWarsWebFactory(12, maxConcurrentRooms: 1);
        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        Guid textId;
        await using (var scope = customFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
            textId = await db.TrainingTexts.Select(item => item.Id).FirstAsync();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ILiveRoomDispatcher>();
            await dispatcher.CreateRoomAsync(new CreateLiveRoomRequest(
                profile.Id,
                profile.DisplayName,
                "Bereits offen",
                "Text",
                LiveRoomMode.Classic,
                LiveRoomVisibility.Code,
                1,
                8));
        }

        var form = await client.GetStringAsync("/arena/neu");
        var token = AntiForgeryRegex().Match(form).Groups["token"].Value;
        var response = await client.PostAsync("/arena/neu", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Title"] = "Ein Raum zu viel",
            ["Input.TrainingTextId"] = textId.ToString(),
            ["Input.Visibility"] = nameof(LiveRoomVisibility.Code),
            ["Input.Mode"] = nameof(LiveRoomMode.Classic),
            ["Input.RoundCount"] = "1",
            ["Input.MaxParticipants"] = "8",
            ["__RequestVerificationToken"] = token
        }));
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.Contains("Die Arena ist gerade ausgelastet", body);
        Assert.Contains("Es wurde kein Raum erstellt", body);
    }

    [Fact]
    public async Task OnboardingPersistsThreeShortStepsAndChosenDestination()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var dashboard = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("In drei Schritten startklar", dashboard);

        var stepOne = await client.GetStringAsync("/onboarding");
        Assert.Contains("Schritt 1 von 3", WebUtility.HtmlDecode(stepOne));
        Assert.Contains("Einrichtung überspringen", WebUtility.HtmlDecode(stepOne));
        var stepOneToken = AntiForgeryRegex().Match(stepOne).Groups["token"].Value;
        var firstResponse = await client.PostAsync("/onboarding?handler=Training", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.PreferredMode"] = nameof(TrainingMode.Words25),
            ["__RequestVerificationToken"] = stepOneToken
        }));
        Assert.Equal("/onboarding?schritt=2", firstResponse.Headers.Location?.OriginalString);

        var stepTwo = await client.GetStringAsync(firstResponse.Headers.Location);
        Assert.Contains("Schritt 2 von 3", WebUtility.HtmlDecode(stepTwo));
        var stepTwoToken = AntiForgeryRegex().Match(stepTwo).Groups["token"].Value;
        var secondResponse = await client.PostAsync("/onboarding?handler=Visibility", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.LeaderboardVisible"] = "true",
            ["Input.ShowLiveWpm"] = "true",
            ["Input.ReducedMotion"] = "true",
            ["__RequestVerificationToken"] = stepTwoToken
        }));
        Assert.Equal("/onboarding?schritt=3", secondResponse.Headers.Location?.OriginalString);

        var stepThree = await client.GetStringAsync(secondResponse.Headers.Location);
        Assert.Contains("Schritt 3 von 3", WebUtility.HtmlDecode(stepThree));
        var stepThreeToken = AntiForgeryRegex().Match(stepThree).Groups["token"].Value;
        var finishResponse = await client.PostAsync("/onboarding?handler=Finish", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Destination"] = nameof(OnboardingModel.OnboardingDestination.TextLibrary),
            ["__RequestVerificationToken"] = stepThreeToken
        }));

        Assert.Equal(HttpStatusCode.Redirect, finishResponse.StatusCode);
        Assert.Equal("/texte", finishResponse.Headers.Location?.OriginalString);
        await using var scope = isolatedFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
        Assert.Equal(TrainingMode.Words25, profile.PreferredMode);
        Assert.True(profile.LeaderboardVisible);
        Assert.True(profile.ShowLiveWpm);
        Assert.True(profile.ReducedMotion);
        Assert.NotNull(profile.OnboardingCompletedAt);
    }

    [Fact]
    public async Task OnboardingCanBeSkipped()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        var page = await client.GetStringAsync("/onboarding");
        var token = AntiForgeryRegex().Match(page).Groups["token"].Value;

        var response = await client.PostAsync("/onboarding?handler=Skip", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var secondVisit = await client.GetAsync("/onboarding");
        Assert.Equal(HttpStatusCode.Redirect, secondVisit.StatusCode);
        Assert.Equal("/", secondVisit.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ArenaCreateFiltersUnsafeTargetsAndRejectsManipulatedSelection()
    {
        using var customFactory = new ConfiguredKeyWarsWebFactory(12, maxArenaTargetGraphemes: 8);
        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        Guid safeId;
        Guid tooLongId;
        await using (var scope = customFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profile = await db.UserProfiles.SingleAsync(item => item.SamAccountName == "max.mustermann");
            var safe = new TrainingText
            {
                OwnerProfileId = profile.Id,
                Title = "Arena-Grenze exakt",
                Body = "12345678",
                CharacterCount = 8,
                Visibility = TrainingTextVisibility.Private
            };
            var tooLong = new TrainingText
            {
                OwnerProfileId = profile.Id,
                Title = "Arena-Grenze überschritten",
                Body = "123456789",
                CharacterCount = 9,
                Visibility = TrainingTextVisibility.Private
            };
            var tooLargeUtf8 = new TrainingText
            {
                OwnerProfileId = profile.Id,
                Title = "Arena-UTF-8 überschritten",
                Body = "a" + new string('\u0308', 7000),
                CharacterCount = 1,
                Visibility = TrainingTextVisibility.Private
            };
            db.TrainingTexts.AddRange(safe, tooLong, tooLargeUtf8);
            await db.SaveChangesAsync();
            safeId = safe.Id;
            tooLongId = tooLong.Id;
        }

        var form = await client.GetStringAsync("/arena/neu");
        var decodedForm = WebUtility.HtmlDecode(form);
        Assert.Contains(safeId.ToString(), form, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Arena-Grenze exakt", decodedForm);
        Assert.DoesNotContain("Arena-Grenze überschritten", decodedForm);
        Assert.DoesNotContain("Arena-UTF-8 überschritten", decodedForm);
        Assert.Contains("sichtbare Texte wurden", decodedForm);

        var token = AntiForgeryRegex().Match(form).Groups["token"].Value;
        var response = await client.PostAsync("/arena/neu", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Title"] = "Manipulierte Auswahl",
            ["Input.TrainingTextId"] = tooLongId.ToString(),
            ["Input.Visibility"] = LiveRoomVisibility.InternalOpen.ToString(),
            ["Input.Mode"] = LiveRoomMode.Classic.ToString(),
            ["Input.RoundCount"] = "1",
            ["Input.MaxParticipants"] = "8",
            ["__RequestVerificationToken"] = token
        }));
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("für eine Live-Arena zu lang", body);
    }

    [Fact]
    public async Task ArenaJoinFormUsesSixCharacterCodeContractAndSubmitGuard()
    {
        using var isolatedFactory = new KeyWarsWebFactory();
        var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        var form = await client.GetStringAsync("/arena/beitreten");

        Assert.Contains("maxlength=\"6\"", form);
        Assert.Contains("minlength=\"6\"", form);
        Assert.Contains("pattern=\"[A-HJ-NP-Z2-9a-hj-np-z]{6}\"", form);
        Assert.Contains("data-room-code-input", form);
        Assert.Contains("data-submit-guard", form);
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

public sealed class ConfiguredKeyWarsWebFactory(
    int maxParticipantsPerRoom,
    int maxArenaTargetGraphemes = LiveOptions.MaximumSafeArenaTargetGraphemes,
    int maxConcurrentRooms = 256) : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"keywars-e2e-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("KEYWARS:DATA:DIRECTORY", dataDirectory);
        builder.UseSetting("KEYWARS:AUTH:DEVELOPMENT_LOGIN", "true");
        builder.UseSetting("KEYWARS:LIVE:MAX_PARTICIPANTS_PER_ROOM", maxParticipantsPerRoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("KEYWARS:LIVE:MAX_ARENA_TARGET_GRAPHEMES", maxArenaTargetGraphemes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("KEYWARS:LIVE:MAX_CONCURRENT_ROOMS", maxConcurrentRooms.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
