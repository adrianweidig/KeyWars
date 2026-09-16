using System.Net;
using KeyWars.Infrastructure.Cluster;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RuntimeTopologyTests
{
    [Fact]
    public void StandaloneDefaultsToSingleProcessWithWorkers()
    {
        var topology = RuntimeTopology.Resolve(new ConfigurationBuilder().Build());

        Assert.Equal(RuntimeRole.All, topology.Role);
        Assert.Equal(KeyWarsDatabaseProvider.Sqlite, topology.DatabaseProvider);
        Assert.True(topology.HostsApplication);
        Assert.True(topology.HostsArena);
        Assert.True(topology.RunsWorkers);
        Assert.True(topology.RunsMigrations);
    }

    [Theory]
    [InlineData("web", true, false, false)]
    [InlineData("arena", true, true, false)]
    [InlineData("worker", false, false, true)]
    [InlineData("migrate", false, false, false)]
    public void ClusterRolesKeepHttpArenaAndWorkerResponsibilitiesSeparate(
        string role,
        bool hostsApplication,
        bool hostsArena,
        bool runsWorkers)
    {
        var topology = RuntimeTopology.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:RUNTIME:ROLE"] = role,
                ["KEYWARS:DATABASE:PROVIDER"] = "postgresql",
                ["ConnectionStrings:KeyWars"] = "Host=postgres;Database=keywars",
                ["KEYWARS:REDIS:CONNECTION_STRING"] = "redis:6379"
            })
            .Build());

        Assert.Equal(hostsApplication, topology.HostsApplication);
        Assert.Equal(hostsArena, topology.HostsArena);
        Assert.Equal(runsWorkers, topology.RunsWorkers);
    }

    [Fact]
    public void SplitRolesAreRejectedWithSqlite()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:RUNTIME:ROLE"] = "web"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => RuntimeTopology.Resolve(configuration));

        Assert.Contains("SQLite unterstützt ausschließlich", error.Message);
    }

    [Fact]
    public void ClusterRejectsAnIncompatibleProtocolVersionBeforeConnecting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:DATABASE:PROVIDER"] = "postgresql",
                ["ConnectionStrings:KeyWars"] = "Host=postgres;Database=keywars",
                ["KEYWARS:REDIS:CONNECTION_STRING"] = "redis:6379",
                ["KEYWARS:CLUSTER:PROTOCOL_VERSION"] = "0"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => RuntimeTopology.Resolve(configuration));

        Assert.Contains(RuntimeTopology.ClusterProtocolVersion, error.Message);
    }

    [Fact]
    public void ClusterProtocolCutoverRequiresTheExplicitConfirmationCommand()
    {
        Assert.True(RuntimeTopology.IsClusterProtocolCutoverCommand(
            ["maintenance", "cluster-protocol", "cutover", "--confirm-apps-stopped"]));
        Assert.False(RuntimeTopology.IsClusterProtocolCutoverCommand(
            ["maintenance", "cluster-protocol", "cutover"]));
        Assert.False(RuntimeTopology.IsClusterProtocolCutoverCommand(
            ["maintenance", "cluster-protocol", "cutover", "--confirm-apps-running"]));
    }

    [Fact]
    public void ClusterProtocolCutoverAcceptsEmptyTaggedAndHistoricalCompletionNamespaces()
    {
        RuntimeTopology.RequireLegacyCompletionAuthorityDrained(
            new LegacyCompletionAuthority(0, 0, 0, 0),
            new LegacyCompletionAuthority(0, 0, 0, 0));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, false)]
    [InlineData(0, 1, 0, 0, false)]
    [InlineData(0, 0, 1, 0, false)]
    [InlineData(0, 0, 0, 1, false)]
    [InlineData(1, 0, 0, 0, true)]
    [InlineData(0, 1, 0, 0, true)]
    [InlineData(0, 0, 1, 0, true)]
    [InlineData(0, 0, 0, 1, true)]
    public void ClusterProtocolCutoverRejectsBothLegacyCompletionNamespacesWithoutChangingTheMarker(
        long pending,
        long failed,
        long enqueued,
        long records,
        bool historical)
    {
        var authority = new LegacyCompletionAuthority(pending, failed, enqueued, records);
        var empty = new LegacyCompletionAuthority(0, 0, 0, 0);
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.RequireLegacyCompletionAuthorityDrained(
                historical ? empty : authority,
                historical ? authority : empty));

        Assert.Contains($"pending={pending}", error.Message);
        Assert.Contains($"failed={failed}", error.Message);
        Assert.Contains($"enqueued={enqueued}", error.Message);
        Assert.Contains($"records={records}", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Fact]
    public void LegacyCompletionPatternsCoverActualTaggedAndHistoricalNamespaces()
    {
        Assert.Equal("keywars:{completion}:pending", RuntimeTopology.LegacyCompletionPendingKey);
        Assert.Equal("keywars:{completion}:failed", RuntimeTopology.LegacyCompletionFailedKey);
        Assert.Equal("keywars:{completion}:enqueued", RuntimeTopology.LegacyCompletionEnqueuedKey);
        Assert.Equal("keywars:{completion}:record:*", RuntimeTopology.LegacyCompletionRecordPattern);
        Assert.Equal("keywars:completion:pending", RuntimeTopology.HistoricalCompletionPendingKey);
        Assert.Equal("keywars:completion:failed", RuntimeTopology.HistoricalCompletionFailedKey);
        Assert.Equal("keywars:completion:enqueued", RuntimeTopology.HistoricalCompletionEnqueuedKey);
        Assert.Equal("keywars:completion:record:*", RuntimeTopology.HistoricalCompletionRecordPattern);
        Assert.DoesNotContain(
            "keywars:{completion}:*",
            RedisClusterProtocolCutover.LegacyCompletionResidualPatterns);
        Assert.Contains(
            "keywars:{completion}:status:*",
            RedisClusterProtocolCutover.LegacyCompletionResidualPatterns);
        Assert.Contains(
            "keywars:completion:redrive:*",
            RedisClusterProtocolCutover.LegacyCompletionResidualPatterns);
    }

    [Fact]
    public void ClusterProtocolV3FreshInstallPreparesMissingMarker()
    {
        Assert.Equal(
            ClusterProtocolCutoverMarkerState.RequiresLegacyPreparation,
            RuntimeTopology.ClassifyClusterProtocolCutoverMarker(null));
    }

    [Fact]
    public void ClusterProtocolV3CutoverAcceptsAtomicUpgradeFromV2()
    {
        Assert.Equal(
            ClusterProtocolCutoverMarkerState.RequiresMarkerUpgrade,
            RuntimeTopology.ClassifyClusterProtocolCutoverMarker(
                RuntimeTopology.PreviousClusterProtocolVersion));
    }

    [Fact]
    public void ClusterProtocolV3CutoverIsIdempotentForV3Marker()
    {
        Assert.Equal(
            ClusterProtocolCutoverMarkerState.AlreadyCurrent,
            RuntimeTopology.ClassifyClusterProtocolCutoverMarker(RuntimeTopology.ClusterProtocolVersion));
    }

    [Fact]
    public void ClusterProtocolV3CutoverRejectsV1UntilTheV2MigrationRan()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.ClassifyClusterProtocolCutoverMarker(
                RuntimeTopology.LegacyClusterProtocolVersion));

        Assert.Contains("zuerst", error.Message);
        Assert.Contains(RuntimeTopology.PreviousClusterProtocolVersion, error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData(" 1 ")]
    public void ClusterProtocolV3CutoverRejectsMarkerMismatch(string activeVersion)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.ClassifyClusterProtocolCutoverMarker(activeVersion));

        Assert.Contains("ausschließlich", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Fact]
    public void ClusterProtocolVersionsDescribeOnlyTheSupportedV2ToV3Transition()
    {
        Assert.Equal("3", RuntimeTopology.ClusterProtocolVersion);
        Assert.Equal("2", RuntimeTopology.PreviousClusterProtocolVersion);
        Assert.Equal("1", RuntimeTopology.LegacyClusterProtocolVersion);
        Assert.Contains("active == ARGV[1]", RuntimeTopology.ClusterProtocolCutoverScript);
        Assert.Contains("ARGV[2]", RuntimeTopology.ClusterProtocolCutoverScript);
    }

    [Fact]
    public void ClusterProtocolMarkerScriptAtomicallyAllowsOnlyFreshOrV2ToV3()
    {
        Assert.Equal(
            "local active = redis.call('GET', KEYS[1])\n" +
            "if not active or active == ARGV[1] then\n" +
            "    redis.call('SET', KEYS[1], ARGV[2])\n" +
            "    return 1\n" +
            "end\n" +
            "if active == ARGV[2] then\n" +
            "    return 0\n" +
            "end\n" +
            "return -1",
            RuntimeTopology.ClusterProtocolCutoverScript.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ClusterProtocolV3CutoverAcceptsAnEmptyV2CompletionAuthority()
    {
        RedisClusterProtocolCutover.RequireCurrentCompletionAuthorityDrained([]);
    }

    [Theory]
    [InlineData("keywars:{completion-b00}:pending")]
    [InlineData("keywars:{completion-bff}:failed")]
    [InlineData("keywars:{completion-b7a}:enqueued")]
    [InlineData("keywars:{completion-b12}:record:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("keywars:{completion-b12}:profiles:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("keywars:{completion-b12}:attempts:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("keywars:{completion-b12}:redrive:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("keywars:{completion-b12}:enqueue-intent:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("keywars:{completion-b12}:lock:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("keywars:{completion-admission}:active")]
    [InlineData("keywars:{completion-admission}:pending")]
    [InlineData("keywars:{completion-admission}:failed")]
    [InlineData("keywars:{completion-admission}:enqueued")]
    [InlineData("keywars:{completion-admission}:reconcile")]
    [InlineData("keywars:{completion-admission}:room-slots")]
    [InlineData("keywars:{completion-admission}:room-slot-tokens")]
    [InlineData("keywars:{completion-admission}:rebuild-token")]
    [InlineData("keywars:{completion-admission}:rebuild-lock")]
    public void ClusterProtocolV3CutoverRejectsEveryV2CompletionAuthorityLayer(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.RequireCurrentCompletionAuthorityDrained([(RedisKey)key]));

        Assert.Contains(key, error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Fact]
    public void ClusterProtocolV3CutoverScansAllV2CompletionAuthorityLayers()
    {
        var patterns = RedisClusterProtocolCutover.CurrentCompletionAuthorityScanPatterns
            .Select(pattern => pattern.ToString())
            .ToArray();

        Assert.Contains("keywars:{completion-b??}:pending", patterns);
        Assert.Contains("keywars:{completion-b??}:failed", patterns);
        Assert.Contains("keywars:{completion-b??}:enqueued", patterns);
        Assert.Contains("keywars:{completion-b??}:record:*", patterns);
        Assert.Contains("keywars:{completion-b??}:profiles:*", patterns);
        Assert.Contains("keywars:{completion-b??}:attempts:*", patterns);
        Assert.Contains("keywars:{completion-b??}:redrive:*", patterns);
        Assert.Contains("keywars:{completion-b??}:lock:*", patterns);
        Assert.Contains("keywars:{completion-b??}:profile-index", patterns);
        Assert.Contains("keywars:{completion-b??}:profile-counts", patterns);
        Assert.Contains("keywars:{completion-b??}:profile-revisions", patterns);
        Assert.Contains("keywars:{completion-admission}:active", patterns);
        Assert.Contains("keywars:{completion-admission}:pending", patterns);
        Assert.Contains("keywars:{completion-admission}:failed", patterns);
        Assert.Contains("keywars:{completion-admission}:enqueued", patterns);
        Assert.Contains("keywars:{completion-admission}:reconcile", patterns);
        Assert.Contains("keywars:{completion-admission}:room-slots", patterns);
        Assert.Contains("keywars:{completion-admission}:room-slot-tokens", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-token", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-active", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-pending", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-failed", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-enqueued", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-reconcile", patterns);
        Assert.Contains("keywars:{completion-admission}:rebuild-lock", patterns);
        Assert.Contains("keywars:{completion-admission}:lock:*", patterns);
    }

    [Fact]
    public void DeletedLegacyProfileMarkerMapsToPermanentBucketedStateKey()
    {
        var profileId = Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef");
        var observedAt = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

        var plan = RedisClusterProtocolCutover.CreateLegacyProfileAccessPlan(
            $"keywars:{{profile-access}}:{profileId:N}:state",
            "deleted",
            TimeSpan.FromHours(3),
            observedAt);

        Assert.NotNull(plan);
        Assert.Equal(LegacyProfileAccessAction.MigrateDeletedState, plan.Action);
        Assert.Equal(
            $"keywars:{{profile-access-b4e}}:state:{profileId:N}",
            plan.DestinationKey?.ToString());
    }

    [Fact]
    public void LegacyProfileActiveLeaseIsPlannedForPurgeOnly()
    {
        var profileId = Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef");

        var plan = RedisClusterProtocolCutover.CreateLegacyProfileAccessPlan(
            $"keywars:{{profile-access}}:{profileId:N}:active",
            RedisValue.Null,
            null,
            DateTimeOffset.UnixEpoch);

        Assert.NotNull(plan);
        Assert.Equal(LegacyProfileAccessAction.PurgeActiveLease, plan.Action);
        Assert.Null(plan.DestinationKey);
    }

    [Theory]
    [InlineData("keywars:{profile-access}:not-a-guid:state")]
    [InlineData("keywars:{profile-access}:0198a1b2c3d47e5f8123456789abcdef:unknown")]
    [InlineData("keywars:{profile-access}:0198A1B2C3D47E5F8123456789ABCDEF:state")]
    [InlineData("keywars:{profile-access}:unexpected")]
    public void UnknownLegacyProfileAccessKeysFailClosed(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.CreateLegacyProfileAccessPlan(
                key,
                "deleted",
                TimeSpan.FromHours(1),
                DateTimeOffset.UnixEpoch));

        Assert.Contains("Unbekannter alter ProfileAccess-Schlüssel", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Theory]
    [InlineData("op:0198a1b2c3d47e5f8123456789abcdef")]
    [InlineData("available")]
    public void NonDeletedLegacyProfileStateFailsClosed(string state)
    {
        var profileId = Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef");
        var error = Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.CreateLegacyProfileAccessPlan(
                $"keywars:{{profile-access}}:{profileId:N}:state",
                state,
                TimeSpan.FromMinutes(1),
                DateTimeOffset.UnixEpoch));

        Assert.Contains("nicht 'deleted'", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Fact]
    public void DeletedLegacyProfileStateWithoutTtlFailsClosed()
    {
        var profileId = Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef");
        var error = Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.CreateLegacyProfileAccessPlan(
                $"keywars:{{profile-access}}:{profileId:N}:state",
                "deleted",
                null,
                DateTimeOffset.UnixEpoch));

        Assert.Contains("keine gültige Rest-TTL", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Fact]
    public void NormalClusterStartFailsClosedWithoutProtocolMarker()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.RequireActiveClusterProtocol(null));

        Assert.Contains(RuntimeTopology.ClusterProtocolCutoverCommand, error.Message);
    }

    [Fact]
    public void NormalClusterStartFailsClosedForDifferentProtocolMarker()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.RequireActiveClusterProtocol("0"));

        Assert.Contains("verwendet Cluster-Protokoll 0", error.Message);
        Assert.Contains(RuntimeTopology.ClusterProtocolVersion, error.Message);
    }

    [Fact]
    public void NormalClusterStartRejectsPreviousProtocolDuringV3Rollout()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.RequireActiveClusterProtocol(
                RuntimeTopology.PreviousClusterProtocolVersion));

        Assert.Contains(RuntimeTopology.PreviousClusterProtocolVersion, error.Message);
        Assert.Contains(RuntimeTopology.ClusterProtocolVersion, error.Message);
    }

    [Fact]
    public void NormalClusterStartAcceptsExactProtocolMarker()
    {
        RuntimeTopology.RequireActiveClusterProtocol(RuntimeTopology.ClusterProtocolVersion);
    }

    [Fact]
    public void CutoverAcceptsExactlyOneConnectedStandalonePrimaryWithStableIdentity()
    {
        var endpoint = new DnsEndPoint("redis", 6379);
        var snapshot = RedisClusterProtocolCutover.CreateStandaloneTopologySnapshot(
        [
            new RedisStandaloneTopologyObservation(
                endpoint,
                ServerType.Standalone,
                IsConnected: true,
                IsReplica: false,
                "# Server\r\nredis_mode:standalone\r\nrun_id:0123456789abcdef0123456789abcdef01234567\r\n",
                "# Replication\r\nrole:master\r\n")
        ]);

        Assert.Equal(RedisCutoverTopologyMode.Standalone, snapshot.Mode);
        Assert.Equal(endpoint, Assert.Single(snapshot.PrimaryEndpoints));
        Assert.Equal(
            "standalone|redis:6379|0123456789abcdef0123456789abcdef01234567",
            snapshot.Signature);
    }

    [Fact]
    public void CutoverRejectsMultipleStandaloneEndpoints()
    {
        var observations = new[]
        {
            StandaloneObservation("redis-a", isReplica: false),
            StandaloneObservation("redis-b", isReplica: false)
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.CreateStandaloneTopologySnapshot(observations));

        Assert.Contains("genau einen", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Theory]
    [InlineData(false, false, "verbunden")]
    [InlineData(true, true, "Primärinstanz")]
    public void CutoverRejectsUnavailableOrReplicaStandalone(
        bool isConnected,
        bool isReplica,
        string expectedMessage)
    {
        var observation = StandaloneObservation("redis", isReplica) with
        {
            IsConnected = isConnected
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.CreateStandaloneTopologySnapshot([observation]));

        Assert.Contains(expectedMessage, error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Theory]
    [InlineData("cluster", "master")]
    [InlineData("standalone", "slave")]
    [InlineData("standalone", "replica")]
    public void CutoverRejectsStandaloneInfoThatDoesNotConfirmPrimary(
        string redisMode,
        string role)
    {
        var observation = StandaloneObservation("redis", isReplica: false) with
        {
            ServerInfo = $"redis_mode:{redisMode}\r\nrun_id:0123456789abcdef0123456789abcdef01234567\r\n",
            ReplicationInfo = $"role:{role}\r\n"
        };

        Assert.Throws<InvalidOperationException>(
            () => RedisClusterProtocolCutover.CreateStandaloneTopologySnapshot([observation]));
    }

    private static RedisStandaloneTopologyObservation StandaloneObservation(
        string host,
        bool isReplica) =>
        new(
            new DnsEndPoint(host, 6379),
            ServerType.Standalone,
            IsConnected: true,
            IsReplica: isReplica,
            "redis_mode:standalone\r\nrun_id:0123456789abcdef0123456789abcdef01234567\r\n",
            "role:master\r\n");
}
