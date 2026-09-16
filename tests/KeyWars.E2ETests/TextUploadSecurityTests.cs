using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KeyWars.E2ETests;

public sealed class TextUploadSecurityTests
{
    [Theory]
    [InlineData("/texte/neu?handler=Upload")]
    [InlineData("/texte/neu/?handler=Upload")]
    public async Task OversizedUploadIsRejectedBeforeRazorModelBinding(string path)
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        using var body = new ByteArrayContent(new byte[200 * 1024]);
        body.Headers.ContentType = new("multipart/form-data");

        var response = await client.PostAsync(path, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedValidUploadsAreRateLimitedPerProfile()
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        var page = await client.GetStringAsync("/texte/neu");
        var token = AntiForgeryToken(page);

        for (var index = 0; index < 10; index++)
        {
            using var response = await UploadAsync(client, token, index);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        using var limited = await UploadAsync(client, token, 10);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ??
            limited.Headers.GetValues("Retry-After").Single());
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token, int index)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(token), "__RequestVerificationToken");
        body.Add(new StringContent("Private"), "UploadVisibility");
        var file = new ByteArrayContent("Kurzer sicherer Testtext."u8.ToArray());
        file.Headers.ContentType = new("text/plain");
        body.Add(file, "Upload", $"sicher-{index}.txt");
        return await client.PostAsync("/texte/neu?handler=Upload", body);
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
