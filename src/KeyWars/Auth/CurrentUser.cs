using System.Security.Claims;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Auth;

public static class KeyWarsClaims
{
    public const string ProfileId = "keywars:profile-id";
}

public sealed class CurrentUser(KeyWarsDbContext db)
{
    public Guid? GetProfileId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(KeyWarsClaims.ProfileId);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public async Task<UserProfile?> GetProfileAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var profileId = GetProfileId(principal);
        if (profileId is null)
        {
            return null;
        }

        return await db.UserProfiles.SingleOrDefaultAsync(profile => profile.Id == profileId && !profile.Deleted, cancellationToken);
    }

    public async Task<UserProfile> RequireProfileAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        return await GetProfileAsync(principal, cancellationToken)
            ?? throw new InvalidOperationException("Die aktuelle Sitzung besitzt kein gültiges KeyWars-Profil.");
    }
}
