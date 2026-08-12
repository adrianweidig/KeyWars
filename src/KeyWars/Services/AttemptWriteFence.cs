using KeyWars.Data;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

internal static class AttemptWriteFence
{
    private const long AdvisoryLockNamespace = unchecked((long)0x415454454D505400);

    public static async Task AcquireAsync(
        KeyWarsDbContext db,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        var advisoryKey = BitConverter.ToInt64(attemptId.ToByteArray(), 0) ^ AdvisoryLockNamespace;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({advisoryKey});",
            cancellationToken);
    }
}
