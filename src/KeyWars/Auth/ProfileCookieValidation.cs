using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace KeyWars.Auth;

internal static class ProfileCookieValidation
{
    internal const string AuthenticationTimeClaim = "auth_time";
    internal static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(2);

    internal static TimeSpan GetAbsoluteLifetime(int configuredHours) =>
        TimeSpan.FromHours(Math.Clamp(configuredHours, 1, 12));

    internal static void Initialize(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var identity = RequireMutableIdentity(principal);
        if (identity.HasClaim(claim => claim.Type == AuthenticationTimeClaim))
        {
            throw new InvalidOperationException("Die Profilsitzung besitzt bereits einen Anmeldezeitpunkt.");
        }

        identity.AddClaim(new Claim(
            AuthenticationTimeClaim,
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        MarkValidated(principal, now);
    }

    internal static bool IsWithinAbsoluteLifetime(
        ClaimsPrincipal? principal,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        return TryGetAuthenticationTime(principal, out var authenticatedAt) &&
               authenticatedAt <= now &&
               now - authenticatedAt < lifetime;
    }

    internal static bool PrepareRenewal(
        ClaimsPrincipal? principal,
        AuthenticationProperties properties,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        if (!TryGetAuthenticationTime(principal, out var authenticatedAt) || authenticatedAt > now)
        {
            return false;
        }

        var absoluteExpiry = authenticatedAt.Add(lifetime);
        if (absoluteExpiry <= now)
        {
            return false;
        }

        properties.IssuedUtc = now;
        properties.ExpiresUtc = absoluteExpiry;
        return true;
    }

    internal static bool IsDue(ClaimsPrincipal? principal, DateTimeOffset now)
    {
        var value = principal?.FindFirstValue(KeyWarsClaims.ProfileValidatedAt);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return true;
        }

        var validatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return validatedAt > now || now - validatedAt >= RevalidationInterval;
    }

    internal static void MarkValidated(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var identity = RequireMutableIdentity(principal);

        foreach (var claim in identity.FindAll(KeyWarsClaims.ProfileValidatedAt).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(
            KeyWarsClaims.ProfileValidatedAt,
            now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
    }

    private static bool TryGetAuthenticationTime(
        ClaimsPrincipal? principal,
        out DateTimeOffset authenticatedAt)
    {
        var value = principal?.FindFirstValue(AuthenticationTimeClaim);
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            try
            {
                authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        authenticatedAt = default;
        return false;
    }

    private static ClaimsIdentity RequireMutableIdentity(ClaimsPrincipal principal) =>
        principal.Identity as ClaimsIdentity
        ?? throw new InvalidOperationException("Die Profilsitzung besitzt keine änderbare ClaimsIdentity.");
}
