using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisProfileExportConcurrencyGate(IConnectionMultiplexer redis) : IProfileExportConcurrencyGate
{
    private const string Subsystem = "profile-export";
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask<IOperationLease?> TryAcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Das Exportprofil darf nicht leer sein.", nameof(profileId));
        }

        return await RedisDistributedLease.TryAcquireAsync(
            database,
            LockKey(profileId),
            cancellationToken);
    }

    internal static RedisKey LockKey(Guid profileId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, profileId)}:lock:{profileId:N}";
}
