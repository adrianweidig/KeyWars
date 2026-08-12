using KeyWars.Infrastructure.Cluster;
using Microsoft.Extensions.Configuration;

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
    public void ClusterProtocolCutoverAcceptsAnEmptyLegacyCompletionQueue()
    {
        RuntimeTopology.RequireLegacyCompletionQueueDrained(0, 0, 0);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(2, 3, 4)]
    public void ClusterProtocolCutoverRejectsLegacyCompletionWorkWithoutChangingTheMarker(
        long pendingJobs,
        long failedRecords,
        long legacyRecordCount)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RuntimeTopology.RequireLegacyCompletionQueueDrained(
                pendingJobs,
                failedRecords,
                legacyRecordCount));

        Assert.Contains($"{pendingJobs} offene", error.Message);
        Assert.Contains($"{failedRecords} fehlgeschlagene", error.Message);
        Assert.Contains($"{legacyRecordCount} gespeicherte", error.Message);
        Assert.Contains("Cluster-Protokollmarker blieb unverändert", error.Message);
    }

    [Fact]
    public void LegacyCompletionQueueKeysRemainThePreCutoverNamespace()
    {
        Assert.Equal("keywars:completion:pending", RuntimeTopology.LegacyCompletionPendingKey);
        Assert.Equal("keywars:completion:failed", RuntimeTopology.LegacyCompletionFailedKey);
        Assert.Equal("keywars:completion:record:*", RuntimeTopology.LegacyCompletionRecordPattern);
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
    public void NormalClusterStartAcceptsExactProtocolMarker()
    {
        RuntimeTopology.RequireActiveClusterProtocol(RuntimeTopology.ClusterProtocolVersion);
    }
}
