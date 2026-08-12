using KeyWars.Data;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

internal static class ChallengeWriteFence
{
    private const long AdvisoryLockNamespace = unchecked((long)0x4348414C4C454E00);

    public static async Task AcquireAsync(
        KeyWarsDbContext db,
        Guid challengeId,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        var advisoryKey = BitConverter.ToInt64(challengeId.ToByteArray(), 0) ^ AdvisoryLockNamespace;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({advisoryKey});",
            cancellationToken);
    }
}
