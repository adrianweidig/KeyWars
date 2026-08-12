using System.Net;
using System.Text.RegularExpressions;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KeyWars.E2ETests;

public sealed class ContentModerationE2ETests
{
    [Fact]
    public async Task ModerationPageRequiresAuthenticationAndModeratorClaim()
    {
        using var factory = new KeyWarsWebFactory();
        var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonymousResponse = await anonymous.GetAsync("/moderation");
        Assert.Equal(HttpStatusCode.Redirect, anonymousResponse.StatusCode);
        Assert.StartsWith("/anmelden", anonymousResponse.Headers.Location?.OriginalString, StringComparison.Ordinal);

        var user = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(user);
        var userResponse = await user.GetAsync("/moderation");
        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);
    }

    [Fact]
    public async Task LdapGroupModeratorCanUnpublishForeignContentThroughProtectedForm()
    {
        using var factory = new ModeratorKeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        Guid textId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var owner = new UserProfile
            {
                DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
                DirectorySid = "S-1-5-21-content-owner",
                SamAccountName = "content.owner",
                UserPrincipalName = "content.owner@example.local",
                DisplayName = "Content Owner"
            };
            var text = new TrainingText
            {
                OwnerProfileId = owner.Id,
                Title = "Zu prüfender Organisationstext",
                SourceKey = $"e2e-{Guid.NewGuid():N}",
                Body = "Dieser Text wird im Moderationstest geprüft.",
                CharacterCount = 43,
                Visibility = TrainingTextVisibility.Organization
            };
            db.AddRange(owner, text);
            await db.SaveChangesAsync();
            textId = text.Id;
        }

        var page = await client.GetAsync("/moderation");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Zu prüfender Organisationstext", html);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")
            .Groups["token"].Value;

        var response = await client.PostAsync(
            "/moderation?handler=Moderate",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.TargetId"] = textId.ToString("D"),
                ["Input.TargetType"] = ContentModerationTargetType.TrainingText.ToString(),
                ["Input.Action"] = ContentModerationAction.Unpublish.ToString(),
                ["Input.Reason"] = "E2E-geprüfter Moderationsgrund",
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var moderated = await verificationDb.TrainingTexts.AsNoTracking().SingleAsync(text => text.Id == textId);
        var audit = await verificationDb.ContentModerationAuditEntries.AsNoTracking().SingleAsync();
        Assert.Equal(TrainingTextVisibility.Private, moderated.Visibility);
        Assert.Equal(textId, audit.TargetId);
        Assert.Equal(ContentModerationAction.Unpublish, audit.Action);
        Assert.Equal("E2E-geprüfter Moderationsgrund", audit.Reason);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/anmelden");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")
            .Groups["token"].Value;
        var response = await client.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = "max.mustermann",
            ["Input.Password"] = "lokales-test-passwort",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private sealed class ModeratorKeyWarsWebFactory : WebApplicationFactory<Program>
    {
        private readonly string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"keywars-moderation-e2e-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("KEYWARS:DATA:DIRECTORY", dataDirectory);
            builder.UseSetting("KEYWARS:AUTH:DEVELOPMENT_LOGIN", "true");
            builder.UseSetting("KEYWARS:MODERATION:MODERATOR_GROUP_VALUES", "KeyWars Moderators");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILdapAuthenticator>();
                services.AddScoped<ILdapAuthenticator, ModeratorAuthenticator>();
            });
        }
    }

    private sealed class ModeratorAuthenticator : ILdapAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            _ = username;
            _ = password;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuthenticationResult.Success(new DirectoryIdentity(
                "22222222-2222-2222-2222-222222222222",
                "S-1-5-21-moderator",
                "max.mustermann",
                "max.mustermann@example.local",
                "Max Mustermann",
                "Max",
                "Mustermann",
                "max.mustermann@example.local",
                "IT",
                "Moderator")
            {
                GroupValues = ["CN=KeyWars Moderators,OU=Groups,DC=example,DC=local"]
            }));
        }
    }
}
