namespace KeyWars.Infrastructure.Cluster;

public enum RuntimeRole
{
    All,
    Web,
    Arena,
    Worker,
    Migrate
}

public enum KeyWarsDatabaseProvider
{
    Sqlite,
    PostgreSql
}

public sealed record RuntimeTopology(
    RuntimeRole Role,
    KeyWarsDatabaseProvider DatabaseProvider,
    string? DatabaseConnectionString,
    string? RedisConnectionString,
    string DataProtectionApplicationName)
{
    public bool IsCluster => DatabaseProvider == KeyWarsDatabaseProvider.PostgreSql;
    public bool HostsHttp => Role != RuntimeRole.Migrate;
    public bool HostsApplication => Role is RuntimeRole.All or RuntimeRole.Web or RuntimeRole.Arena;
    public bool HostsArena => Role is RuntimeRole.All or RuntimeRole.Arena;
    public bool RunsWorkers => Role is RuntimeRole.All or RuntimeRole.Worker;
    public bool RunsMigrations => Role == RuntimeRole.Migrate || !IsCluster && Role == RuntimeRole.All;
    public bool UsesRuntimeFileLock => !IsCluster;

    public static RuntimeTopology Resolve(IConfiguration configuration)
    {
        var role = ParseRole(configuration["KEYWARS:RUNTIME:ROLE"]);
        var provider = ParseProvider(configuration["KEYWARS:DATABASE:PROVIDER"]);
        var databaseConnectionString = FirstNonEmpty(
            configuration.GetConnectionString("KeyWars"),
            configuration["KEYWARS:DATABASE:CONNECTION_STRING"]);
        var redisConnectionString = configuration["KEYWARS:REDIS:CONNECTION_STRING"]?.Trim();
        var applicationName = configuration["KEYWARS:DATAPROTECTION:APPLICATION_NAME"]?.Trim();
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = "KeyWars";
        }

        if (provider == KeyWarsDatabaseProvider.Sqlite)
        {
            if (role is not (RuntimeRole.All or RuntimeRole.Migrate))
            {
                throw new InvalidOperationException(
                    "SQLite unterstützt ausschließlich KEYWARS__RUNTIME__ROLE=all oder migrate. " +
                    "Web-, Arena- und Worker-Replikate benötigen PostgreSQL und Redis.");
            }

            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                throw new InvalidOperationException(
                    "KEYWARS__REDIS__CONNECTION_STRING ist im SQLite-Standalone-Modus nicht zulässig. " +
                    "Setze KEYWARS__DATABASE__PROVIDER=postgresql für den Scale-Modus.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(databaseConnectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings__KeyWars ist im PostgreSQL-Modus erforderlich.");
            }

            if (string.IsNullOrWhiteSpace(redisConnectionString))
            {
                throw new InvalidOperationException(
                    "KEYWARS__REDIS__CONNECTION_STRING ist im PostgreSQL-Modus erforderlich.");
            }
        }

        return new RuntimeTopology(
            role,
            provider,
            databaseConnectionString,
            redisConnectionString,
            applicationName);
    }

    private static RuntimeRole ParseRole(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "all" : value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "all" => RuntimeRole.All,
            "web" => RuntimeRole.Web,
            "arena" => RuntimeRole.Arena,
            "worker" => RuntimeRole.Worker,
            "migrate" => RuntimeRole.Migrate,
            _ => throw new InvalidOperationException(
                "KEYWARS__RUNTIME__ROLE muss all, web, arena, worker oder migrate sein.")
        };
    }

    private static KeyWarsDatabaseProvider ParseProvider(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "sqlite" : value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "sqlite" => KeyWarsDatabaseProvider.Sqlite,
            "postgres" or "postgresql" => KeyWarsDatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException(
                "KEYWARS__DATABASE__PROVIDER muss sqlite oder postgresql sein.")
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
