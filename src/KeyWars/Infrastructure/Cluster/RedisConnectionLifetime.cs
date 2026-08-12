using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

internal sealed class RedisConnectionLifetime(IConnectionMultiplexer connection) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await connection.CloseAsync();
        connection.Dispose();
    }
}
