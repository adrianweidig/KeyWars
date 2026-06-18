using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Auth;

public sealed class ProfileProvisioner(KeyWarsDbContext db, TimeProvider timeProvider)
{
    public async Task<UserProfile> ProvisionAsync(DirectoryIdentity identity, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var profile = await db.UserProfiles
            .SingleOrDefaultAsync(item => item.DirectoryObjectGuid == identity.ObjectGuid, cancellationToken);

        if (profile is null)
        {
            profile = new UserProfile
            {
                DirectoryObjectGuid = identity.ObjectGuid,
                CreatedAt = now,
                AccentKey = AccentFor(identity.ObjectGuid)
            };
            db.UserProfiles.Add(profile);
        }

        profile.DirectorySid = identity.ObjectSid;
        profile.SamAccountName = identity.SamAccountName;
        profile.UserPrincipalName = identity.UserPrincipalName;
        profile.DisplayName = identity.DisplayName;
        profile.GivenName = identity.GivenName;
        profile.Surname = identity.Surname;
        profile.Email = identity.Email;
        profile.Department = identity.Department;
        profile.Title = identity.Title;
        profile.UpdatedAt = now;
        profile.LastLoginAt = now;
        profile.Deleted = false;

        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private static string AccentFor(string stableValue)
    {
        var accents = new[] { "cyan", "green", "yellow", "rose", "violet", "blue" };
        return accents[Math.Abs(stableValue.GetHashCode(StringComparison.Ordinal)) % accents.Length];
    }
}
