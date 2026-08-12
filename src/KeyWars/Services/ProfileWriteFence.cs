using KeyWars.Data;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

internal static class ProfileWriteFence
{
    public static async Task AcquireAsync(
        KeyWarsDbContext db,
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        foreach (var profileId in profileIds.Distinct().Order())
        {
            var advisoryKey = BitConverter.ToInt64(profileId.ToByteArray(), 0);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey});",
                cancellationToken);
        }
    }

    public static Task AcquireAsync(
        KeyWarsDbContext db,
        Guid profileId,
        CancellationToken cancellationToken) =>
        AcquireAsync(db, [profileId], cancellationToken);

    public static async Task<bool> IsAvailableAsync(
        KeyWarsDbContext db,
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        var ids = profileIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return true;
        }

        var availableCount = await db.UserProfiles
            .AsNoTracking()
            .CountAsync(profile => ids.Contains(profile.Id) && !profile.Deleted, cancellationToken);
        return availableCount == ids.Length;
    }

    public static Task<bool> IsAvailableAsync(
        KeyWarsDbContext db,
        Guid profileId,
        CancellationToken cancellationToken) =>
        IsAvailableAsync(db, [profileId], cancellationToken);
}
