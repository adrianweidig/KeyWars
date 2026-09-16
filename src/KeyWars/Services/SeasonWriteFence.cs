using KeyWars.Data;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

internal static class SeasonWriteFence
{
    private const long PostgresLock = 0x4B57534541534F4E;
    private static readonly AsyncKeyedLock<string> SqliteLocks = new();

    public static async ValueTask<IAsyncDisposable?> AcquireAsync(
        KeyWarsDbContext db,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            if (db.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("Saison-Schreibvorgänge benötigen eine Datenbanktransaktion.");
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({PostgresLock});",
                cancellationToken);
            return null;
        }

        if (!db.Database.IsSqlite())
        {
            return null;
        }

        var dataSource = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            dataSource = db.Database.GetDbConnection().ConnectionString;
        }
        else
        {
            dataSource = Path.GetFullPath(dataSource);
        }

        return await SqliteLocks.AcquireAsync(dataSource, cancellationToken);
    }
}
