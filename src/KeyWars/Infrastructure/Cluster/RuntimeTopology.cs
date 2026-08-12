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
    public const string ClusterProtocolVersion = "1";
    public const string ClusterProtocolVersionKey = "keywars:cluster:protocol-version";
    public const string LegacyCompletionPendingKey = "keywars:completion:pending";
    public const string LegacyCompletionFailedKey = "keywars:completion:failed";
    public const string LegacyCompletionRecordPattern = "keywars:completion:record:*";
    public const string ClusterProtocolCutoverCommand =
        "maintenance cluster-protocol cutover --confirm-apps-stopped";
    public const string ClusterProtocolCutoverScript = """
        local active = redis.call('GET', KEYS[1])
        if not active then
            redis.call('SET', KEYS[1], ARGV[1])
            return 1
        end
        if active == ARGV[1] then
            return 0
        end
        return -1
        """;

    public bool IsCluster => DatabaseProvider == KeyWarsDatabaseProvider.PostgreSql;
    public bool HostsHttp => Role != RuntimeRole.Migrate;
    public bool HostsApplication => Role is RuntimeRole.All or RuntimeRole.Web or RuntimeRole.Arena;
    public bool HostsArena => Role is RuntimeRole.All or RuntimeRole.Arena;
    public bool RunsWorkers => Role is RuntimeRole.All or RuntimeRole.Worker;
    public bool RunsMigrations => Role == RuntimeRole.Migrate || !IsCluster && Role == RuntimeRole.All;
    public bool UsesRuntimeFileLock => !IsCluster;

    public static bool IsClusterProtocolCutoverCommand(IReadOnlyList<string> arguments) =>
        arguments.Count == 4 &&
        string.Equals(arguments[0], "maintenance", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[1], "cluster-protocol", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[2], "cutover", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[3], "--confirm-apps-stopped", StringComparison.Ordinal);

    public static void RequireActiveClusterProtocol(string? activeVersion)
    {
        if (activeVersion is null)
        {
            throw new InvalidOperationException(
                $"Der Redis-Keyspace ist noch nicht auf Cluster-Protokoll {ClusterProtocolVersion} vorbereitet. " +
                $"Stoppe alle KeyWars-Anwendungsreplikate und führe einmalig `{ClusterProtocolCutoverCommand}` mit Rolle migrate aus.");
        }

        if (!string.Equals(activeVersion, ClusterProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dieser Redis-Keyspace verwendet Cluster-Protokoll {activeVersion}; " +
                $"dieses Image benötigt {ClusterProtocolVersion}. " +
                "Der Marker blieb unverändert. Stoppe alle KeyWars-Anwendungsreplikate und folge dem " +
                "versionsspezifischen Cutover in den Release Notes.");
        }
    }

    public static void RequireLegacyCompletionQueueDrained(
        long pendingJobs,
        long failedRecords,
        long legacyRecordCount)
    {
        if (pendingJobs == 0 && failedRecords == 0 && legacyRecordCount == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Der alte Redis-Completion-Namespace enthält noch " +
            $"{pendingJobs} offene, {failedRecords} fehlgeschlagene und " +
            $"{legacyRecordCount} gespeicherte Abschlussaufträge. " +
            "Starte das bisherige Release erneut, lasse die Abschlussqueue vollständig leerlaufen und " +
            "stoppe danach alle Anwendungsreplikate. Der Cluster-Protokollmarker blieb unverändert.");
    }

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

            var configuredProtocolVersion = configuration["KEYWARS:CLUSTER:PROTOCOL_VERSION"]?.Trim();
            if (!string.IsNullOrEmpty(configuredProtocolVersion) &&
                configuredProtocolVersion != ClusterProtocolVersion)
            {
                throw new InvalidOperationException(
                    $"KEYWARS__CLUSTER__PROTOCOL_VERSION muss {ClusterProtocolVersion} sein.");
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
