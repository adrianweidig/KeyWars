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

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var dashboard = await client.GetStringAsync("/");
        Assert.Contains("Max Mustermann", dashboard);
        Assert.Contains("Sofort tippen", dashboard);
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
