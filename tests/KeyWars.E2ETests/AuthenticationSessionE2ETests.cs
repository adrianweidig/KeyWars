using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using KeyWars.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KeyWars.E2ETests;

public sealed partial class AuthenticationSessionE2ETests
{
    [Fact]
    public async Task LoginTicketCarriesImmutableAuthenticationTimeAndAbsoluteExpiry()
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var loginPage = await client.GetStringAsync("/anmelden");
        var token = AntiForgeryRegex().Match(loginPage).Groups["token"].Value;

        var response = await client.PostAsync("/anmelden", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.Username"] = "max.mustermann",
                ["Input.Password"] = "lokales-test-passwort",
                ["__RequestVerificationToken"] = token
            }));

        var cookieHeader = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("KeyWars.Dev.Auth=", StringComparison.Ordinal));
        var protectedTicket = Uri.UnescapeDataString(cookieHeader
            .Split(';', 2)[0]
            .Split('=', 2)[1]);
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = options.TicketDataFormat.Unprotect(protectedTicket);

        Assert.NotNull(ticket);
        Assert.False(options.SlidingExpiration);
        Assert.True(ticket.Properties.IssuedUtc.HasValue);
        Assert.True(ticket.Properties.ExpiresUtc.HasValue);
        var issuedAt = ticket.Properties.IssuedUtc.Value;
        var expiresAt = ticket.Properties.ExpiresUtc.Value;
        Assert.Equal(TimeSpan.FromHours(8), expiresAt - issuedAt);
        Assert.Equal(
            issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ticket.Principal.FindFirst("auth_time")?.Value);
        Assert.Equal(
            issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ticket.Principal.FindFirst(KeyWarsClaims.ProfileValidatedAt)?.Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")]
    private static partial Regex AntiForgeryRegex();
}
