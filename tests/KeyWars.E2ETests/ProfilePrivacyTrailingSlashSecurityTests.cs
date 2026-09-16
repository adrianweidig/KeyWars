using System.Net;
using System.Text.RegularExpressions;
using KeyWars.Data;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyWars.E2ETests;

public sealed class ProfilePrivacyTrailingSlashSecurityTests
{
    [Theory]
    [InlineData("/profil/statistik-zuruecksetzen/", "/profil", false)]
    [InlineData("/profil/loeschen/", "/anmelden", true)]
    public async Task TrailingSlashPrivacyPostWaitsForExistingLeaseWithoutSelfDeadlock(
        string path,
        string expectedLocation,
        bool deletesProfile)
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        using var page = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var token = AntiForgeryToken(await page.Content.ReadAsStringAsync());

        Guid profileId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            profileId = await db.UserProfiles
                .Where(profile => profile.SamAccountName == "max.mustermann" && !profile.Deleted)
                .Select(profile => profile.Id)
                .SingleAsync();
        }

        var gate = factory.Services.GetRequiredService<IProfileAccessGate>();
        var existingLease = await gate.AcquireAsync(profileId);
        try
        {
            var post = client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Confirmation"] = "max.mustermann",
                ["__RequestVerificationToken"] = token
            }));
            await WaitForStateAsync(gate, profileId, ProfileAccessState.OperationInProgress);
            Assert.False(post.IsCompleted);
            var rejected = await Assert.ThrowsAsync<ProfileOperationException>(async () =>
            {
                await using var lease = await gate.AcquireAsync(profileId);
            });
            Assert.Equal("profile_operation_in_progress", rejected.Code);

            await existingLease.DisposeAsync();
            using var response = await post.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(expectedLocation, response.Headers.Location?.ToString());
        }
        finally
        {
            await existingLease.DisposeAsync();
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profileDeleted = await verificationDb.UserProfiles
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.Deleted)
            .SingleAsync();
        Assert.Equal(deletesProfile, profileDeleted);
    }

    private static async Task WaitForStateAsync(
        IProfileAccessGate gate,
        Guid profileId,
        ProfileAccessState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await gate.GetStateAsync(profileId, timeout.Token) != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/anmelden");
        using var response = await client.PostAsync("/anmelden", new FormUrlEncodedContent(
            new Dictionary<string, string>
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
