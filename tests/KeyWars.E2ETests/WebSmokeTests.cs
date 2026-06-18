using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
        var login = await client.GetStringAsync("/anmelden");
        var token = AntiForgeryRegex().Match(login).Groups["token"].Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = "max.mustermann",
            ["Input.Password"] = "lokales-test-passwort",
            ["__RequestVerificationToken"] = token
        });

        var response = await client.PostAsync("/anmelden", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var dashboard = await client.GetStringAsync("/");
        Assert.Contains("Max Mustermann", dashboard);
        Assert.Contains("Sofort tippen", dashboard);
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
