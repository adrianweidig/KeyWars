using System.Security.Claims;
using KeyWars.Auth;
using Microsoft.AspNetCore.Authentication;

namespace KeyWars.UnitTests;

public sealed class ProfileCookieValidationTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(8, 8)]
    [InlineData(99, 12)]
    public void AbsoluteLifetimeIsClampedToTheSupportedSecurityBoundary(
        int configuredHours,
        int expectedHours)
    {
        Assert.Equal(
            TimeSpan.FromHours(expectedHours),
            ProfileCookieValidation.GetAbsoluteLifetime(configuredHours));
    }

    [Fact]
    public void MissingInvalidFutureAndExpiredTimestampsRequireValidation()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ProfileCookieValidation.IsDue(Principal(), now));
        Assert.True(ProfileCookieValidation.IsDue(Principal("invalid"), now));
        Assert.True(ProfileCookieValidation.IsDue(
            Principal(now.AddSeconds(1).ToUnixTimeSeconds().ToString()),
            now));
        Assert.True(ProfileCookieValidation.IsDue(
            Principal(now.Subtract(ProfileCookieValidation.RevalidationInterval).ToUnixTimeSeconds().ToString()),
            now));
    }

    [Fact]
    public void FreshTimestampSkipsTheRelationalRevalidation()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var principal = Principal(now.AddMinutes(-1).ToUnixTimeSeconds().ToString());

        Assert.False(ProfileCookieValidation.IsDue(principal, now));
    }

    [Fact]
    public void MarkValidatedReplacesAnyPreviousTimestamp()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var principal = Principal("1");

        ProfileCookieValidation.MarkValidated(principal, now);

        var claim = Assert.Single(principal.FindAll(KeyWarsClaims.ProfileValidatedAt));
        Assert.Equal(now.ToUnixTimeSeconds().ToString(), claim.Value);
    }

    [Fact]
    public void AuthenticationTimeIsCreatedOnceAndNeverChangedByRevalidation()
    {
        var authenticatedAt = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
        var principal = Principal();

        ProfileCookieValidation.Initialize(principal, authenticatedAt);
        ProfileCookieValidation.MarkValidated(principal, authenticatedAt.AddHours(1));

        var claim = Assert.Single(principal.FindAll(ProfileCookieValidation.AuthenticationTimeClaim));
        Assert.Equal(authenticatedAt.ToUnixTimeSeconds().ToString(), claim.Value);
        Assert.Throws<InvalidOperationException>(
            () => ProfileCookieValidation.Initialize(principal, authenticatedAt.AddHours(2)));
    }

    [Fact]
    public void RenewalKeepsTheOriginalAbsoluteExpiryAndRejectsTheHorizon()
    {
        var authenticatedAt = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
        var lifetime = TimeSpan.FromHours(8);
        var principal = Principal();
        ProfileCookieValidation.Initialize(principal, authenticatedAt);
        var properties = new AuthenticationProperties();

        Assert.True(ProfileCookieValidation.IsWithinAbsoluteLifetime(
            principal,
            authenticatedAt.AddHours(7),
            lifetime));
        Assert.True(ProfileCookieValidation.PrepareRenewal(
            principal,
            properties,
            authenticatedAt.AddHours(7),
            lifetime));
        Assert.Equal(authenticatedAt.AddHours(7), properties.IssuedUtc);
        Assert.Equal(authenticatedAt.AddHours(8), properties.ExpiresUtc);

        Assert.False(ProfileCookieValidation.IsWithinAbsoluteLifetime(
            principal,
            authenticatedAt.AddHours(8),
            lifetime));
        Assert.False(ProfileCookieValidation.PrepareRenewal(
            principal,
            properties,
            authenticatedAt.AddHours(8),
            lifetime));
        Assert.Equal(
            authenticatedAt.ToUnixTimeSeconds().ToString(),
            Assert.Single(principal.FindAll(ProfileCookieValidation.AuthenticationTimeClaim)).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("1786531201")]
    public void MissingInvalidAndFutureAuthenticationTimesFailClosed(string? authenticationTime)
    {
        var now = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
        var claims = authenticationTime is null
            ? Array.Empty<Claim>()
            : [new Claim(ProfileCookieValidation.AuthenticationTimeClaim, authenticationTime)];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        Assert.False(ProfileCookieValidation.IsWithinAbsoluteLifetime(
            principal,
            now,
            TimeSpan.FromHours(8)));
    }

    private static ClaimsPrincipal Principal(string? timestamp = null)
    {
        var claims = timestamp is null
            ? Array.Empty<Claim>()
            : [new Claim(KeyWarsClaims.ProfileValidatedAt, timestamp)];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
