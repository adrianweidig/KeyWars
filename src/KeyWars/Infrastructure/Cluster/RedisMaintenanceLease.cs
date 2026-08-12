using KeyWars.Data;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisMaintenanceLease(IConnectionMultiplexer redis) : IMaintenanceLease
{
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask<IOperationLease?> TryAcquireAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (operation.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Der Maintenance-Name enthält ungültige Zeichen.", nameof(operation));
        }

        return await RedisDistributedLease.TryAcquireAsync(
            database,
            $"keywars:maintenance:lock:{operation.ToLowerInvariant()}",
            cancellationToken);
    }
}
