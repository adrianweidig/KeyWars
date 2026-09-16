using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KeyWars.E2ETests;

public sealed class LoginRateLimitSecurityTests
{
    [Fact]
    public async Task TrailingSlashLoginIsRoutedAndRateLimited()
    {
        using var factory = new StrictLoginRateLimitFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var page = await client.GetAsync("/anmelden/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var token = AntiForgeryToken(await page.Content.ReadAsStringAsync());

        for (var index = 0; index < 10; index++)
        {
            using var response = await PostInvalidLoginAsync(client, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var limited = await PostInvalidLoginAsync(client, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ??
            limited.Headers.GetValues("Retry-After").Single());
    }

    private static Task<HttpResponseMessage> PostInvalidLoginAsync(HttpClient client, string token) =>
        client.PostAsync("/anmelden/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = string.Empty,
            ["Input.Password"] = string.Empty,
            ["__RequestVerificationToken"] = token
        }));

    private static string AntiForgeryToken(string html)
    {
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"",
            RegexOptions.CultureInvariant).Groups["token"].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private sealed class StrictLoginRateLimitFactory : WebApplicationFactory<Program>
    {
        private readonly string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"keywars-login-rate-limit-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("KEYWARS:DATA:DIRECTORY", dataDirectory);
            builder.UseSetting("KEYWARS:AUTH:DEVELOPMENT_LOGIN", "false");
        }
    }
}
