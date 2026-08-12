using System.Security.Claims;

namespace KeyWars.Auth;

public static class ContentModeratorClaims
{
    public static bool IsModerator(ClaimsPrincipal principal) =>
        principal.HasClaim(KeyWarsClaims.ContentModerator, "true");

    public static IEnumerable<Claim> Create(
        DirectoryIdentity identity,
        ContentModerationOptions options)
    {
        if (IsModerator(identity, options))
        {
            yield return new Claim(KeyWarsClaims.ContentModerator, "true");
        }
    }

    public static bool IsModerator(
        DirectoryIdentity identity,
        ContentModerationOptions options)
    {
        var actualGroups = identity.GroupValues;
        if (actualGroups.Count == 0)
        {
            return false;
        }

        var configuredDns = Split(options.ModeratorGroupDns);
        if (configuredDns.Any(expected =>
                actualGroups.Any(actual => actual.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        var configuredValues = Split(options.ModeratorGroupValues);
        return configuredValues.Any(expected => actualGroups.Any(actual =>
            actual.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            FirstRdnValue(actual).Equals(expected, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstRdnValue(string distinguishedName)
    {
        var separator = distinguishedName.IndexOf('=');
        if (separator < 0 || separator == distinguishedName.Length - 1)
        {
            return distinguishedName.Trim();
        }

        var value = distinguishedName[(separator + 1)..];
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == ',' && !escaped)
            {
                value = value[..index];
                break;
            }

            escaped = current == '\\' && !escaped;
            if (current != '\\')
            {
                escaped = false;
            }
        }

        return value.Replace("\\,", ",", StringComparison.Ordinal).Trim();
    }
}

public static class KeyWarsPolicies
{
    public const string ContentModerator = "keywars-content-moderator";
}
