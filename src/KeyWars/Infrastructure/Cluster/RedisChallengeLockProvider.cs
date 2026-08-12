using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisChallengeLockProvider(IConnectionMultiplexer redis) : IChallengeLockProvider
{
    private readonly IDatabase database = redis.GetDatabase();

    public ValueTask<IAsyncDisposable> AcquireAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default) =>
        RedisDistributedLease.AcquireAsync(
            database,
            $"keywars:challenge:lock:{challengeId:N}",
            cancellationToken);
}
