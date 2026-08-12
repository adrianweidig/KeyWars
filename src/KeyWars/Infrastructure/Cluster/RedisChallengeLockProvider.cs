using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisChallengeLockProvider(IConnectionMultiplexer redis) : IChallengeLockProvider
{
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask<IOperationLease> AcquireAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default) =>
        await RedisDistributedLease.AcquireAsync(
            database,
            $"keywars:challenge:lock:{challengeId:N}",
            cancellationToken);
}
