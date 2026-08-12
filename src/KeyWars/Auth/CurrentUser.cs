using System.Security.Claims;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Auth;

public static class KeyWarsClaims
{
    public const string ProfileId = "keywars:profile-id";
    public const string ContentModerator = "keywars:content-moderator";
}

public sealed class CurrentUser(KeyWarsDbContext db, IProfileAccessGate? accessGate = null)
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

        if (accessGate is not null &&
            await accessGate.GetStateAsync(profileId.Value, cancellationToken) != ProfileAccessState.Available)
        {
            return null;
        }

        var profile = await db.UserProfiles.SingleOrDefaultAsync(profile => profile.Id == profileId && !profile.Deleted, cancellationToken);
        return accessGate is not null &&
            await accessGate.GetStateAsync(profileId.Value, cancellationToken) != ProfileAccessState.Available
                ? null
                : profile;
    }

    public async Task<UserProfile> RequireProfileAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        return await GetProfileAsync(principal, cancellationToken)
            ?? throw new InvalidOperationException("Die aktuelle Sitzung besitzt kein gültiges KeyWars-Profil.");
    }
}
