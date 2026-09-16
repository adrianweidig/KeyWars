using System.Data;
using System.Net;
using Npgsql;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

internal static class RedisClusterProtocolCutover
{
    internal const string LegacyAttemptNamespacePattern = "keywars:{attempt}:*";
    internal const string LegacyPresenceNamespacePattern = "keywars:{presence}:*";
    internal const string LegacyProgressNamespacePattern = "keywars:{progress}:*";
    internal const string LegacyProfileAccessNamespacePattern = "keywars:{profile-access}:*";
    internal static readonly string[] LegacyCompletionResidualPatterns =
    [
        "keywars:{completion}:status:*",
        "keywars:{completion}:lock:*",
        "keywars:{completion}:attempts:*",
        "keywars:{completion}:redrive:*",
        "keywars:completion:status:*",
        "keywars:completion:lock:*",
        "keywars:completion:attempts:*",
        "keywars:completion:redrive:*"
    ];
    internal static readonly RedisValue[] CurrentCompletionAuthorityScanPatterns =
    [
        "keywars:{completion-b??}:pending",
        "keywars:{completion-b??}:failed",
        "keywars:{completion-b??}:enqueued",
        "keywars:{completion-b??}:record:*",
        "keywars:{completion-b??}:profiles:*",
        "keywars:{completion-b??}:attempts:*",
        "keywars:{completion-b??}:redrive:*",
        "keywars:{completion-b??}:enqueue-intent:*",
        "keywars:{completion-b??}:lock:*",
        "keywars:{completion-b??}:profile-index",
        "keywars:{completion-b??}:profile-counts",
        "keywars:{completion-b??}:profile-revisions",
        "keywars:{completion-admission}:active",
        "keywars:{completion-admission}:pending",
        "keywars:{completion-admission}:failed",
        "keywars:{completion-admission}:enqueued",
        "keywars:{completion-admission}:reconcile",
        "keywars:{completion-admission}:room-slots",
        "keywars:{completion-admission}:room-slot-tokens",
        "keywars:{completion-admission}:rebuild-token",
        "keywars:{completion-admission}:rebuild-active",
        "keywars:{completion-admission}:rebuild-pending",
        "keywars:{completion-admission}:rebuild-failed",
        "keywars:{completion-admission}:rebuild-enqueued",
        "keywars:{completion-admission}:rebuild-reconcile",
        "keywars:{completion-admission}:rebuild-lock",
        "keywars:{completion-admission}:lock:*"
    ];
    private const string LegacyProfileAccessPrefix = "keywars:{profile-access}:";
    private const string DeletedState = "deleted";
    private static readonly LuaScript PersistDeletedMarkerScript = LuaScript.Prepare(
        "local state = redis.call('get', @destinationKey); " +
        "if state and state ~= 'deleted' then return -1 end; " +
        "redis.call('set', @destinationKey, 'deleted'); " +
        "if state then return 0 else return 1 end");
    private static readonly RedisValue[] LegacyScanPatterns =
    [
        LegacyAttemptNamespacePattern,
        LegacyPresenceNamespacePattern,
        LegacyProgressNamespacePattern,
        LegacyProfileAccessNamespacePattern,
        "keywars:{completion}:*",
        "keywars:completion:*"
    ];

    internal static async Task<RedisClusterProtocolCutoverResult> ExecuteAsync(
        IConnectionMultiplexer redis,
        int databaseNumber,
        string postgresConnectionString,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var database = redis.GetDatabase(databaseNumber);
        var activeVersion = await ReadProtocolVersionAsync(database, cancellationToken);
        var markerState = RuntimeTopology.ClassifyClusterProtocolCutoverMarker(activeVersion);
        if (markerState == ClusterProtocolCutoverMarkerState.AlreadyCurrent)
        {
            return new RedisClusterProtocolCutoverResult(true, 0, 0);
        }

        var currentCompletionBefore = await CaptureStableCurrentCompletionAuthorityAsync(
            redis,
            databaseNumber,
            cancellationToken);
        RequireCurrentCompletionAuthorityDrained(currentCompletionBefore.Keys);

        var migratedDeletedMarkers = 0;
        var purgedTransientKeys = 0;
        if (markerState == ClusterProtocolCutoverMarkerState.RequiresLegacyPreparation)
        {
            await using var attemptGuard = await PostgresAttemptCutoverGuard.AcquireAsync(
                postgresConnectionString,
                cancellationToken);
            var legacySnapshot = await CaptureStableLegacyKeyspaceAsync(
                redis,
                databaseNumber,
                cancellationToken);
            await RequireCompletionAuthorityEmptyAsync(database, legacySnapshot, cancellationToken);

            // Finish all discovery and validation before deleting any v1 transient state.
            var profilePlans = await BuildProfileAccessPlansAsync(
                database,
                legacySnapshot.Keys,
                timeProvider,
                cancellationToken);
            var transientKeys = legacySnapshot.Keys
                .Where(IsLegacyTransientKey)
                .ToArray();
            var completionResidualKeys = ClassifyCompletionKeys(legacySnapshot.Keys);

            await ValidateExistingDestinationsAsync(database, profilePlans, timeProvider, cancellationToken);

            foreach (var plan in profilePlans.Where(item => item.Action == LegacyProfileAccessAction.MigrateDeletedState))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await MigrateDeletedMarkerAsync(database, plan, timeProvider, cancellationToken))
                {
                    migratedDeletedMarkers++;
                }
            }

            var purgeKeys = transientKeys
                .Concat(completionResidualKeys)
                .Concat(profilePlans
                    .Where(item => item.Action == LegacyProfileAccessAction.PurgeActiveLease)
                    .Select(item => item.SourceKey))
                .Distinct()
                .OrderBy(key => key.ToString(), StringComparer.Ordinal)
                .ToArray();
            foreach (var key in purgeKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await database.KeyDeleteAsync(key))
                {
                    purgedTransientKeys++;
                }
            }

            var finalSnapshot = await CaptureStableLegacyKeyspaceAsync(
                redis,
                databaseNumber,
                cancellationToken);
            RequireLegacyTransientStateRemoved(finalSnapshot.Keys);
            await RequireCompletionAuthorityEmptyAsync(database, finalSnapshot, cancellationToken);
        }
        else
        {
            var legacySnapshot = await CaptureStableLegacyKeyspaceAsync(
                redis,
                databaseNumber,
                cancellationToken);
            RequireLegacyTransientStateRemoved(legacySnapshot.Keys);
            await RequireCompletionAuthorityEmptyAsync(database, legacySnapshot, cancellationToken);
        }

        var currentCompletionAfter = await CaptureStableCurrentCompletionAuthorityAsync(
            redis,
            databaseNumber,
            cancellationToken);
        RequireCurrentCompletionAuthorityDrained(currentCompletionAfter.Keys);

        cancellationToken.ThrowIfCancellationRequested();
        var cutoverResult = (long)await database.ScriptEvaluateAsync(
            RuntimeTopology.ClusterProtocolCutoverScript,
            [RuntimeTopology.ClusterProtocolVersionKey],
            [RuntimeTopology.PreviousClusterProtocolVersion, RuntimeTopology.ClusterProtocolVersion]);
        if (cutoverResult < 0)
        {
            var incompatibleVersion = await ReadProtocolVersionAsync(database, cancellationToken);
            _ = RuntimeTopology.ClassifyClusterProtocolCutoverMarker(incompatibleVersion);
            throw new InvalidOperationException("Der Cluster-Protokollmarker konnte nicht atomar aktualisiert werden.");
        }

        if (cutoverResult is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"Der Cluster-Protokoll-Cutover lieferte das unerwartete Ergebnis {cutoverResult}.");
        }

        return new RedisClusterProtocolCutoverResult(
            cutoverResult == 0,
            purgedTransientKeys,
            migratedDeletedMarkers);
    }

    internal static LegacyProfileAccessPlan? CreateLegacyProfileAccessPlan(
        string sourceKey,
        RedisValue stateValue,
        TimeSpan? remainingTtl,
        DateTimeOffset observedAt)
    {
        var parsed = ParseLegacyProfileAccessKey(sourceKey);
        if (parsed.Kind == LegacyProfileAccessKeyKind.Active)
        {
            return new LegacyProfileAccessPlan(
                sourceKey,
                LegacyProfileAccessAction.PurgeActiveLease,
                null);
        }

        if (stateValue.IsNull)
        {
            return null;
        }

        if (stateValue != DeletedState)
        {
            throw new InvalidOperationException(
                $"Der alte ProfileAccess-State für Profil {parsed.ProfileId:N} ist nicht 'deleted'. " +
                "Kurzlebige Operationen müssen auslaufen, bevor der Cutover erneut gestartet wird. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        if (remainingTtl is null || remainingTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Der alte 'deleted'-Marker für Profil {parsed.ProfileId:N} besitzt keine gültige Rest-TTL. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        return new LegacyProfileAccessPlan(
            sourceKey,
            LegacyProfileAccessAction.MigrateDeletedState,
            RedisProfileAccessGate.StateKey(parsed.ProfileId));
    }

    private static LegacyProfileAccessKey ParseLegacyProfileAccessKey(string key)
    {
        if (!key.StartsWith(LegacyProfileAccessPrefix, StringComparison.Ordinal))
        {
            throw UnknownLegacyProfileAccessKey(key);
        }

        var parts = key[LegacyProfileAccessPrefix.Length..].Split(':', StringSplitOptions.None);
        if (parts.Length != 2 ||
            !Guid.TryParseExact(parts[0], "N", out var profileId) ||
            !string.Equals(parts[0], profileId.ToString("N"), StringComparison.Ordinal))
        {
            throw UnknownLegacyProfileAccessKey(key);
        }

        var kind = parts[1] switch
        {
            "state" => LegacyProfileAccessKeyKind.State,
            "active" => LegacyProfileAccessKeyKind.Active,
            _ => throw UnknownLegacyProfileAccessKey(key)
        };
        return new LegacyProfileAccessKey(profileId, kind);
    }

    private static InvalidOperationException UnknownLegacyProfileAccessKey(string key) =>
        new(
            $"Unbekannter alter ProfileAccess-Schlüssel '{key}'. " +
            "Der Schlüssel wurde nicht verändert und der Cluster-Protokollmarker blieb unverändert.");

    private static async Task<IReadOnlyList<LegacyProfileAccessPlan>> BuildProfileAccessPlansAsync(
        IDatabase database,
        IReadOnlyList<RedisKey> legacyKeys,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var keys = legacyKeys
            .Where(key => key.ToString().StartsWith(LegacyProfileAccessPrefix, StringComparison.Ordinal))
            .ToArray();
        var plans = new List<LegacyProfileAccessPlan>(keys.Length);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = ParseLegacyProfileAccessKey(key.ToString());
            if (parsed.Kind == LegacyProfileAccessKeyKind.Active)
            {
                plans.Add(CreateLegacyProfileAccessPlan(
                    key.ToString(),
                    RedisValue.Null,
                    null,
                    timeProvider.GetUtcNow())!);
                continue;
            }

            var state = await database.StringGetWithExpiryAsync(key);
            var plan = CreateLegacyProfileAccessPlan(
                key.ToString(),
                state.Value,
                state.Expiry,
                timeProvider.GetUtcNow());
            if (plan is not null)
            {
                plans.Add(plan);
            }
        }

        return plans;
    }

    private static async Task ValidateExistingDestinationsAsync(
        IDatabase database,
        IReadOnlyList<LegacyProfileAccessPlan> plans,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans.Where(item => item.Action == LegacyProfileAccessAction.MigrateDeletedState))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = await database.StringGetWithExpiryAsync(plan.DestinationKey!.Value);
            if (destination.Value.IsNull)
            {
                continue;
            }

            ValidateDeletedDestination(plan, destination);
        }
    }

    private static async Task<bool> MigrateDeletedMarkerAsync(
        IDatabase database,
        LegacyProfileAccessPlan discoveredPlan,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = await database.StringGetWithExpiryAsync(discoveredPlan.SourceKey);
        var plan = CreateLegacyProfileAccessPlan(
            discoveredPlan.SourceKey.ToString(),
            source.Value,
            source.Expiry,
            timeProvider.GetUtcNow());
        if (plan is null)
        {
            return false;
        }

        var persisted = (int)await database.ScriptEvaluateAsync(
            PersistDeletedMarkerScript,
            new { destinationKey = plan.DestinationKey!.Value });
        if (persisted < 0)
        {
            throw new InvalidOperationException(
                $"Der Zielschlüssel '{plan.DestinationKey}' enthält einen fremden Zustand. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        var destination = await database.StringGetWithExpiryAsync(plan.DestinationKey.Value);
        ValidateDeletedDestination(plan, destination);

        await database.KeyDeleteAsync(plan.SourceKey);
        return true;
    }

    private static void ValidateDeletedDestination(
        LegacyProfileAccessPlan plan,
        RedisValueWithExpiry destination)
    {
        if (destination.Value != DeletedState || destination.Expiry is not null)
        {
            throw new InvalidOperationException(
                $"Der Zielschlüssel '{plan.DestinationKey}' enthält keinen gültigen, dauerhaften " +
                "'deleted'-Marker. Der Cluster-Protokollmarker blieb unverändert.");
        }
    }

    private static async Task RequireCompletionAuthorityEmptyAsync(
        IDatabase database,
        StableLegacyKeyspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var taggedRecordCount = snapshot.Keys.Count(key =>
            key.ToString().StartsWith("keywars:{completion}:record:", StringComparison.Ordinal));
        var historicalRecordCount = snapshot.Keys.Count(key =>
            key.ToString().StartsWith("keywars:completion:record:", StringComparison.Ordinal));
        var tagged = new LegacyCompletionAuthority(
            await database.SortedSetLengthAsync(RuntimeTopology.LegacyCompletionPendingKey),
            await database.SortedSetLengthAsync(RuntimeTopology.LegacyCompletionFailedKey),
            await database.SortedSetLengthAsync(RuntimeTopology.LegacyCompletionEnqueuedKey),
            taggedRecordCount);
        var historical = new LegacyCompletionAuthority(
            await database.SortedSetLengthAsync(RuntimeTopology.HistoricalCompletionPendingKey),
            await database.SortedSetLengthAsync(RuntimeTopology.HistoricalCompletionFailedKey),
            await database.SortedSetLengthAsync(RuntimeTopology.HistoricalCompletionEnqueuedKey),
            historicalRecordCount);
        RuntimeTopology.RequireLegacyCompletionAuthorityDrained(tagged, historical);
    }

    private static void RequireLegacyTransientStateRemoved(
        IReadOnlyList<RedisKey> keys)
    {
        var remaining = keys.Count(IsLegacyTransientKey) +
            keys.Count(key => key.ToString().StartsWith(LegacyProfileAccessPrefix, StringComparison.Ordinal)) +
            ClassifyCompletionKeys(keys).Count;
        if (remaining != 0)
        {
            throw new InvalidOperationException(
                $"Nach der Vorbereitung sind noch {remaining} alte transiente Redis-Schlüssel vorhanden. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }
    }

    private static bool IsLegacyTransientKey(RedisKey key)
    {
        var value = key.ToString();
        return value.StartsWith("keywars:{attempt}:", StringComparison.Ordinal) ||
            value.StartsWith("keywars:{presence}:", StringComparison.Ordinal) ||
            value.StartsWith("keywars:{progress}:", StringComparison.Ordinal);
    }

    private static IReadOnlyList<RedisKey> ClassifyCompletionKeys(IReadOnlyList<RedisKey> keys)
    {
        var residual = new List<RedisKey>();
        foreach (var key in keys)
        {
            var value = key.ToString();
            var tagged = value.StartsWith("keywars:{completion}:", StringComparison.Ordinal);
            var historical = value.StartsWith("keywars:completion:", StringComparison.Ordinal);
            if (!tagged && !historical)
            {
                continue;
            }

            if (value is RuntimeTopology.LegacyCompletionPendingKey or
                RuntimeTopology.LegacyCompletionFailedKey or
                RuntimeTopology.LegacyCompletionEnqueuedKey or
                RuntimeTopology.HistoricalCompletionPendingKey or
                RuntimeTopology.HistoricalCompletionFailedKey or
                RuntimeTopology.HistoricalCompletionEnqueuedKey ||
                value.StartsWith("keywars:{completion}:record:", StringComparison.Ordinal) ||
                value.StartsWith("keywars:completion:record:", StringComparison.Ordinal))
            {
                continue;
            }

            if (LegacyCompletionResidualPatterns.Any(pattern =>
                value.StartsWith(pattern[..^1], StringComparison.Ordinal)))
            {
                residual.Add(key);
                continue;
            }

            throw new InvalidOperationException(
                $"Unbekannter alter Completion-Schlüssel '{value}'. Der Schlüssel wurde nicht verändert " +
                "und der Cluster-Protokollmarker blieb unverändert.");
        }

        return residual;
    }

    private static Task<StableLegacyKeyspaceSnapshot> CaptureStableLegacyKeyspaceAsync(
        IConnectionMultiplexer redis,
        int databaseNumber,
        CancellationToken cancellationToken)
        => CaptureStableKeyspaceAsync(
            redis,
            databaseNumber,
            LegacyScanPatterns,
            "Legacy",
            cancellationToken);

    private static Task<StableLegacyKeyspaceSnapshot> CaptureStableCurrentCompletionAuthorityAsync(
        IConnectionMultiplexer redis,
        int databaseNumber,
        CancellationToken cancellationToken)
        => CaptureStableKeyspaceAsync(
            redis,
            databaseNumber,
            CurrentCompletionAuthorityScanPatterns,
            "Completion-v2",
            cancellationToken);

    private static async Task<StableLegacyKeyspaceSnapshot> CaptureStableKeyspaceAsync(
        IConnectionMultiplexer redis,
        int databaseNumber,
        IReadOnlyList<RedisValue> patterns,
        string scanName,
        CancellationToken cancellationToken)
    {
        var before = await CaptureClusterTopologyAsync(redis, cancellationToken);
        var firstKeys = await ScanPrimaryKeysAsync(
            redis,
            databaseNumber,
            before.PrimaryEndpoints,
            before.Mode,
            patterns,
            cancellationToken);
        var between = await CaptureClusterTopologyAsync(redis, cancellationToken);
        RequireStableScan(
            before.Signature,
            between.Signature,
            between.Signature,
            firstKeys.Select(key => key.ToString()).ToArray(),
            firstKeys.Select(key => key.ToString()).ToArray(),
            scanName);

        var secondKeys = await ScanPrimaryKeysAsync(
            redis,
            databaseNumber,
            before.PrimaryEndpoints,
            before.Mode,
            patterns,
            cancellationToken);
        var after = await CaptureClusterTopologyAsync(redis, cancellationToken);
        RequireStableScan(
            before.Signature,
            between.Signature,
            after.Signature,
            firstKeys.Select(key => key.ToString()).ToArray(),
            secondKeys.Select(key => key.ToString()).ToArray(),
            scanName);
        return new StableLegacyKeyspaceSnapshot(firstKeys, before.Signature);
    }

    internal static void RequireStableLegacyScan(
        string topologyBefore,
        string topologyBetween,
        string topologyAfter,
        IReadOnlyCollection<string> firstKeys,
        IReadOnlyCollection<string> secondKeys) =>
        RequireStableScan(
            topologyBefore,
            topologyBetween,
            topologyAfter,
            firstKeys,
            secondKeys,
            "Legacy");

    private static void RequireStableScan(
        string topologyBefore,
        string topologyBetween,
        string topologyAfter,
        IReadOnlyCollection<string> firstKeys,
        IReadOnlyCollection<string> secondKeys,
        string scanName)
    {
        if (!string.Equals(topologyBefore, topologyBetween, StringComparison.Ordinal) ||
            !string.Equals(topologyBefore, topologyAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Die Redis-Cluster-Topologie hat sich während des doppelten {scanName}-SCANs verändert. " +
                "Failover oder Resharding müssen vollständig beendet sein, bevor der Cutover erneut startet. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        if (!firstKeys.Order(StringComparer.Ordinal).SequenceEqual(
                secondKeys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Die beiden {scanName}-SCANs lieferten unterschiedliche Schlüssel. " +
                "Der Redis-Keyspace war während der Vorbereitung nicht stabil; " +
                "der Cluster-Protokollmarker blieb unverändert.");
        }
    }

    internal static void RequireCurrentCompletionAuthorityDrained(IReadOnlyCollection<RedisKey> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var sample = string.Join(", ", keys
            .Select(key => key.ToString())
            .Order(StringComparer.Ordinal)
            .Take(8));
        throw new InvalidOperationException(
            $"Die Completion-v2-Authority ist nicht vollständig leer oder enthält noch aktive " +
            $"Admission-Arbeit ({keys.Count} Schlüssel; {sample}). Lasse das vorherige Release die " +
            "Abschlussqueue vollständig und fehlerfrei drainen, stoppe danach alle Anwendungsreplikate " +
            "und wiederhole den Cutover. Der Cluster-Protokollmarker blieb unverändert.");
    }

    private static async Task<RedisClusterTopologySnapshot> CaptureClusterTopologyAsync(
        IConnectionMultiplexer redis,
        CancellationToken cancellationToken)
    {
        var endpoints = redis.GetEndPoints().OrderBy(EndpointText, StringComparer.Ordinal).ToArray();
        var endpointStates = endpoints
            .Select(endpoint =>
            {
                var server = redis.GetServer(endpoint);
                return new RedisStandaloneTopologyObservation(
                    endpoint,
                    server.ServerType,
                    server.IsConnected,
                    server.IsReplica,
                    null,
                    null);
            })
            .ToArray();
        if (endpointStates.Any(item => item.ServerType == ServerType.Standalone))
        {
            if (endpointStates.Length == 1 && endpointStates[0].ServerType == ServerType.Standalone)
            {
                var server = redis.GetServer(endpointStates[0].Endpoint);
                endpointStates[0] = endpointStates[0] with
                {
                    ServerInfo = await server.InfoRawAsync("server"),
                    ReplicationInfo = await server.InfoRawAsync("replication")
                };
            }

            return CreateStandaloneTopologySnapshot(endpointStates);
        }

        var views = new List<RedisClusterTopologySnapshot>();
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = redis.GetServer(endpoint);
            if (server.ServerType != ServerType.Cluster || !server.IsConnected)
            {
                throw new InvalidOperationException(
                    $"Redis-Endpunkt '{EndpointText(endpoint)}' ist während des Cutovers nicht als " +
                    "verbundener Cluster-Knoten verfügbar. Der Cluster-Protokollmarker blieb unverändert.");
            }

            var configuration = await server.ClusterNodesAsync()
                ?? throw new InvalidOperationException(
                    $"Redis-Endpunkt '{EndpointText(endpoint)}' lieferte keine Cluster-Topologie. " +
                    "Der Cluster-Protokollmarker blieb unverändert.");
            views.Add(CreateTopologySnapshot(configuration));
        }

        if (views.Count == 0)
        {
            throw new InvalidOperationException(
                "Der Redis-Cutover konnte keine Cluster-Topologie lesen. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        var canonical = views[0];
        if (views.Skip(1).Any(view =>
                !string.Equals(view.Signature, canonical.Signature, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Die Redis-Knoten melden unterschiedliche Cluster-Topologien. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        return canonical;
    }

    internal static RedisClusterTopologySnapshot CreateStandaloneTopologySnapshot(
        IReadOnlyList<RedisStandaloneTopologyObservation> observations)
    {
        if (observations.Count != 1 ||
            observations[0].ServerType != ServerType.Standalone)
        {
            throw new InvalidOperationException(
                "Der Standalone-Redis-Cutover benötigt genau einen konfigurierten Endpunkt. " +
                "Mehrfachendpunkte und gemischte Redis-Topologien werden nicht automatisch interpretiert. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        var observation = observations[0];
        if (!observation.IsConnected)
        {
            throw new InvalidOperationException(
                $"Redis-Endpunkt '{EndpointText(observation.Endpoint)}' ist während des Cutovers nicht verbunden. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        if (observation.IsReplica)
        {
            throw new InvalidOperationException(
                $"Redis-Endpunkt '{EndpointText(observation.Endpoint)}' ist keine Standalone-Primärinstanz. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        var redisMode = ReadRequiredInfoValue(observation.ServerInfo, "redis_mode");
        var runId = ReadRequiredInfoValue(observation.ServerInfo, "run_id");
        var role = ReadRequiredInfoValue(observation.ReplicationInfo, "role");
        if (!string.Equals(redisMode, "standalone", StringComparison.Ordinal) ||
            !string.Equals(role, "master", StringComparison.Ordinal) ||
            runId.Length != 40 ||
            runId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                $"Redis-Endpunkt '{EndpointText(observation.Endpoint)}' bestätigt keine stabile " +
                "Standalone-Primärinstanz. Der Cluster-Protokollmarker blieb unverändert.");
        }

        return new RedisClusterTopologySnapshot(
            string.Join('|', "standalone", EndpointText(observation.Endpoint), runId.ToLowerInvariant()),
            [observation.Endpoint],
            RedisCutoverTopologyMode.Standalone);
    }

    private static string ReadRequiredInfoValue(string? rawInfo, string key)
    {
        var prefix = key + ":";
        var values = (rawInfo ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length != 1)
        {
            throw new InvalidOperationException(
                $"Redis INFO lieferte kein eindeutiges Feld '{key}'. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        return values[0];
    }

    private static RedisClusterTopologySnapshot CreateTopologySnapshot(ClusterConfiguration configuration)
    {
        if (configuration.Nodes.Count == 0)
        {
            throw new InvalidOperationException(
                "Redis meldet eine leere Cluster-Topologie. Der Cluster-Protokollmarker blieb unverändert.");
        }

        var nodeSignatures = new List<string>(configuration.Nodes.Count);
        var primaryEndpoints = new List<EndPoint>();
        foreach (var node in configuration.Nodes.OrderBy(item => item.NodeId, StringComparer.Ordinal))
        {
            if (node.IsFail || node.IsPossiblyFail || node.IsHandshake || node.IsNoAddr || !node.IsConnected)
            {
                throw new InvalidOperationException(
                    $"Redis-Knoten '{node.NodeId}' ist während des Cutovers nicht stabil verbunden. " +
                    "Der Cluster-Protokollmarker blieb unverändert.");
            }

            var raw = node.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (raw.Length < 8 ||
                !string.Equals(raw[0], node.NodeId, StringComparison.Ordinal) ||
                !long.TryParse(raw[6], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var epoch) ||
                epoch < 0)
            {
                throw new InvalidOperationException(
                    $"Die Cluster-Epoch von Redis-Knoten '{node.NodeId}' konnte nicht sicher gelesen werden. " +
                    "Der Cluster-Protokollmarker blieb unverändert.");
            }

            var flags = raw[2]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(flag => !string.Equals(flag, "myself", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);
            var slots = raw.Skip(8).Order(StringComparer.Ordinal);
            var role = node.IsReplica ? "replica" : "primary";
            nodeSignatures.Add(string.Join('|',
                node.NodeId,
                raw[1],
                role,
                node.ParentNodeId ?? "-",
                epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(',', flags),
                raw[7],
                string.Join(',', slots)));
            if (!node.IsReplica)
            {
                primaryEndpoints.Add(node.EndPoint
                    ?? throw new InvalidOperationException(
                        $"Redis-Primärknoten '{node.NodeId}' besitzt keinen auflösbaren Endpunkt. " +
                        "Der Cluster-Protokollmarker blieb unverändert."));
            }
        }

        if (primaryEndpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "Redis meldet keinen Primärknoten für den Cluster-SCAN. " +
                "Der Cluster-Protokollmarker blieb unverändert.");
        }

        return new RedisClusterTopologySnapshot(
            string.Join('\n', nodeSignatures),
            primaryEndpoints.OrderBy(EndpointText, StringComparer.Ordinal).ToArray(),
            RedisCutoverTopologyMode.Cluster);
    }

    private static async Task<IReadOnlyList<RedisKey>> ScanPrimaryKeysAsync(
        IConnectionMultiplexer redis,
        int databaseNumber,
        IReadOnlyList<EndPoint> primaryEndpoints,
        RedisCutoverTopologyMode topologyMode,
        IReadOnlyList<RedisValue> patterns,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in primaryEndpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = redis.GetServer(endpoint);
            var expectedServerType = topologyMode == RedisCutoverTopologyMode.Cluster
                ? ServerType.Cluster
                : ServerType.Standalone;
            if (server.ServerType != expectedServerType ||
                server.IsReplica ||
                !server.IsConnected ||
                (topologyMode == RedisCutoverTopologyMode.Standalone && primaryEndpoints.Count != 1))
            {
                throw new InvalidOperationException(
                    $"Redis-Endpunkt '{EndpointText(endpoint)}' ist vor dem Cutover-SCAN nicht mehr Primärknoten. " +
                    "Der Cluster-Protokollmarker blieb unverändert.");
            }

            foreach (var pattern in patterns)
            {
                await foreach (var key in server.KeysAsync(
                    databaseNumber,
                    pattern,
                    pageSize: 250).WithCancellation(cancellationToken))
                {
                    keys.Add(key.ToString());
                }
            }
        }

        return keys
            .Order(StringComparer.Ordinal)
            .Select(key => (RedisKey)key)
            .ToArray();
    }

    private static string EndpointText(EndPoint endpoint) => endpoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        _ => endpoint.ToString() ?? string.Empty
    };

    private static async Task<string?> ReadProtocolVersionAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await database.StringGetAsync(RuntimeTopology.ClusterProtocolVersionKey);
        return value.IsNull ? null : value.ToString();
    }

    private sealed record LegacyProfileAccessKey(Guid ProfileId, LegacyProfileAccessKeyKind Kind);
}

internal sealed class PostgresAttemptCutoverGuard : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;

    private PostgresAttemptCutoverGuard(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    internal static async Task<PostgresAttemptCutoverGuard> AcquireAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var connection = new NpgsqlConnection(connectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            bool attemptTableExists;
            await using (var existenceCommand = new NpgsqlCommand(
                "SELECT EXISTS (" +
                "SELECT 1 FROM pg_catalog.pg_class c " +
                "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE c.relname = 'TypingAttempts' " +
                "AND n.nspname = ANY (current_schemas(false)) " +
                "AND c.relkind IN ('r', 'p'))",
                connection,
                transaction))
            {
                existenceCommand.CommandTimeout = 15;
                attemptTableExists = await existenceCommand.ExecuteScalarAsync(cancellationToken) is true;
            }

            if (!attemptTableExists)
            {
                return new PostgresAttemptCutoverGuard(connection, transaction);
            }

            await using (var lockCommand = new NpgsqlCommand(
                "LOCK TABLE \"TypingAttempts\" IN SHARE MODE NOWAIT",
                connection,
                transaction))
            {
                lockCommand.CommandTimeout = 15;
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            long activeAttemptCount;
            await using (var countCommand = new NpgsqlCommand(
                "SELECT COUNT(*)::bigint FROM \"TypingAttempts\" " +
                "WHERE \"Phase\" IN ('Prepared', 'Started')",
                connection,
                transaction))
            {
                countCommand.CommandTimeout = 15;
                var value = await countCommand.ExecuteScalarAsync(cancellationToken);
                activeAttemptCount = value is long count
                    ? count
                    : throw new InvalidOperationException(
                        "PostgreSQL lieferte für aktive TypingAttempts keinen gültigen Zählwert. " +
                        "Der Cluster-Protokollmarker blieb unverändert.");
            }

            RuntimeTopology.RequireNoActiveTypingAttempts(activeAttemptCount);
            return new PostgresAttemptCutoverGuard(connection, transaction);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}

internal sealed record RedisClusterProtocolCutoverResult(
    bool AlreadyCurrent,
    int PurgedTransientKeys,
    int MigratedDeletedMarkers);

internal sealed record LegacyProfileAccessPlan(
    RedisKey SourceKey,
    LegacyProfileAccessAction Action,
    RedisKey? DestinationKey);

internal sealed record StableLegacyKeyspaceSnapshot(
    IReadOnlyList<RedisKey> Keys,
    string TopologySignature);

internal sealed record RedisClusterTopologySnapshot(
    string Signature,
    IReadOnlyList<EndPoint> PrimaryEndpoints,
    RedisCutoverTopologyMode Mode);

internal sealed record RedisStandaloneTopologyObservation(
    EndPoint Endpoint,
    ServerType ServerType,
    bool IsConnected,
    bool IsReplica,
    string? ServerInfo,
    string? ReplicationInfo);

internal enum RedisCutoverTopologyMode
{
    Cluster,
    Standalone
}

internal enum LegacyProfileAccessAction
{
    PurgeActiveLease,
    MigrateDeletedState
}

internal enum LegacyProfileAccessKeyKind
{
    State,
    Active
}
