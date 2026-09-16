using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KeyWars.E2ETests;

public sealed class ProfileExportSecurityTests
{
    [Fact]
    public async Task RepeatedProfileExportIsRateLimitedWithRetryAfter()
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        using var first = await client.GetAsync("/profil/export?handler=Download");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var limited = await client.GetAsync("/profil/export?handler=Download");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ??
            limited.Headers.GetValues("Retry-After").Single());
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/anmelden");
        var response = await client.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = "max.mustermann",
            ["Input.Password"] = "lokales-test-passwort",
            ["__RequestVerificationToken"] = AntiForgeryToken(login)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string AntiForgeryToken(string html)
    {
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"",
            RegexOptions.CultureInvariant).Groups["token"].Value;
        Assert.NotEmpty(token);
        return token;
    }
}
