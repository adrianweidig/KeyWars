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
}
