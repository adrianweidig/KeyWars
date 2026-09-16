using System.Reflection;
using KeyWars.Infrastructure.Cluster;

namespace KeyWars.ConcurrencyTests;

public sealed class RedisLiveStateShardingConcurrencyTests
{
    [Fact]
    public async Task BucketWakeSchedulerDoesNotLoseOrDuplicateConcurrentBuckets()
    {
        var schedulerType = typeof(RedisLiveProgressRelay).Assembly.GetType(
            "KeyWars.Infrastructure.Cluster.RedisDueBucketScheduler",
            throwOnError: true)!;
        var scheduler = Activator.CreateInstance(schedulerType, nonPublic: true)!;
        var activate = schedulerType.GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tryTake = schedulerType.GetMethod("TryTake", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 1_024; index++)
            {
                activate.Invoke(scheduler, [(byte)((index + worker) & 255)]);
            }
        })));

        var seen = new HashSet<byte>();
        while (true)
        {
            object?[] arguments = [(byte)0];
            if (!(bool)tryTake.Invoke(scheduler, arguments)!)
            {
                break;
            }

            Assert.True(seen.Add((byte)arguments[0]!));
        }

        Assert.Equal(256, seen.Count);
    }
}
