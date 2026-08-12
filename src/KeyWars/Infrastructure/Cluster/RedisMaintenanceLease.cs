using KeyWars.Data;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisMaintenanceLease(IConnectionMultiplexer redis) : IMaintenanceLease
{
    private readonly IDatabase database = redis.GetDatabase();

    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (operation.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Der Maintenance-Name enthält ungültige Zeichen.", nameof(operation));
        }

        return RedisDistributedLease.TryAcquireAsync(
            database,
            $"keywars:maintenance:lock:{operation.ToLowerInvariant()}",
            cancellationToken);
    }
}
