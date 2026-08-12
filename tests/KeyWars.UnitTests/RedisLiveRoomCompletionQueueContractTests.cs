using System.Reflection;
using KeyWars.Infrastructure.Cluster;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisLiveRoomCompletionQueueContractTests
{
    [Fact]
    public void ParkedFailuresPreserveRecordAndRetryState()
    {
        var source = GetScript("FailureScript").OriginalScript;

        Assert.Contains("get', @lockKey", source);
        Assert.Contains("zrem', @pendingKey", source);
        Assert.Contains("zadd', @failedKey", source);
        Assert.Contains("del', @attemptsKey", source);
        Assert.DoesNotContain("del', @recordKey", source);
    }

    [Fact]
    public void SuccessfulRedriveCleansEveryQueueInventoryAtomically()
    {
        var source = GetScript("CompleteScript").OriginalScript;

        Assert.Contains("get', @lockKey", source);
        Assert.Contains("del', @recordKey, @attemptsKey, @redriveKey", source);
        Assert.Contains("zrem', @pendingKey", source);
        Assert.Contains("zrem', @failedKey", source);
        Assert.Contains("zrem', @enqueuedKey", source);
        Assert.Contains("set', @statusKey, @status, 'PX'", source);
    }

    [Fact]
    public void MissingRecordCleanupPreservesAConcurrentEnqueue()
    {
        var source = GetScript("CleanupMissingScript").OriginalScript;
        var recordCheck = source.IndexOf("exists', @recordKey", StringComparison.Ordinal);
        var pendingRemoval = source.IndexOf("zrem', @pendingKey", StringComparison.Ordinal);

        Assert.True(recordCheck >= 0);
        Assert.Contains("exists', @recordKey) == 1 then return 2", source);
        Assert.True(recordCheck < pendingRemoval);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(6, 900)]
    [InlineData(100, 900)]
    public void RedriveBackoffIsExponentialAndCapped(long cycle, int expectedSeconds)
    {
        var method = typeof(RedisLiveRoomCompletionQueue).GetMethod(
            "CalculateRedriveDelay",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Redrive-Zeitplan fehlt.");

        var delay = Assert.IsType<TimeSpan>(method.Invoke(null, [cycle]));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    private static LuaScript GetScript(string name) =>
        Assert.IsType<LuaScript>(typeof(RedisLiveRoomCompletionQueue)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null));

}
