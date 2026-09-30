using System.Reflection;
using KeyWars.Infrastructure;
using KeyWars.Infrastructure.Cluster;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisProfileHubConnectionStateStoreContractTests
{
    [Fact]
    public void EveryProfileScriptKeySharesTheStableProfileBucket()
    {
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var generation = RedisProfileHubConnectionStateStore.GenerationKey(profileId).ToString();
        var index = RedisProfileHubConnectionStateStore.IndexKey(profileId).ToString();
        var connection = RedisProfileHubConnectionStateStore.ConnectionKey(profileId, "private-connection-id").ToString();

        Assert.Equal(
            "keywars:{profile-connections-ba8}:generation:00112233445566778899aabbccddeeff",
            generation);
        Assert.Equal(
            "keywars:{profile-connections-ba8}:index:00112233445566778899aabbccddeeff",
            index);
        Assert.StartsWith(
            "keywars:{profile-connections-ba8}:connection:00112233445566778899aabbccddeeff:",
            connection,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-connection-id", connection, StringComparison.Ordinal);
    }

    [Fact]
    public void LuaStateMachineKeepsGenerationDurableAndFencesEveryWrite()
    {
        var register = ScriptNamed("RegisterScript");
        var update = ScriptNamed("UpdateScript");
        var unregister = ScriptNamed("UnregisterScript");
        var revoke = ScriptNamed("RevokeScript");

        Assert.Contains("generation ~= tonumber(@expectedGeneration)", register, StringComparison.Ordinal);
        Assert.Contains("redis.call('set', @generationKey, generation)", register, StringComparison.Ordinal);
        Assert.Contains("redis.call('set', @connectionKey, @payload, 'PX'", register, StringComparison.Ordinal);
        Assert.DoesNotContain("pexpire', @generationKey", register, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generation ~= tonumber(@expectedGeneration)", update, StringComparison.Ordinal);
        Assert.Contains("exists', @connectionKey", update, StringComparison.Ordinal);
        Assert.Contains("redis.call('incr', @generationKey)", revoke, StringComparison.Ordinal);
        Assert.Contains("@maximumConnections", register, StringComparison.Ordinal);
        Assert.Contains("@maximumDirectoryEntries", revoke, StringComparison.Ordinal);
        Assert.DoesNotContain("expire', @generationKey", revoke, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@generationKey", unregister, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationRefreshesWellBeforeConnectionExpiry()
    {
        var reconciliation = StaticTimeSpan(typeof(ProfileHubConnectionRegistry), "ReconciliationInterval");
        var connectionLifetime = StaticTimeSpan(typeof(RedisProfileHubConnectionStateStore), "ConnectionLifetime");
        var indexLifetime = StaticTimeSpan(typeof(RedisProfileHubConnectionStateStore), "IndexLifetime");

        Assert.True(reconciliation <= TimeSpan.FromSeconds(5));
        Assert.True(connectionLifetime >= reconciliation * 12);
        Assert.True(indexLifetime > connectionLifetime);
    }

    private static string ScriptNamed(string fieldName) =>
        Assert.IsType<LuaScript>(typeof(RedisProfileHubConnectionStateStore)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)).OriginalScript;

    private static TimeSpan StaticTimeSpan(Type type, string fieldName) =>
        Assert.IsType<TimeSpan>(type
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null));
}
