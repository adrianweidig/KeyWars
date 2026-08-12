using System.Security.Cryptography;
using System.Text;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisSharedRateLimiter(IConnectionMultiplexer redis) : ISharedRateLimiter
{
    private static readonly LuaScript IncrementScript = LuaScript.Prepare(
        "local count = redis.call('incr', @key); " +
        "if count == 1 then redis.call('pexpire', @key, @windowMilliseconds) end; " +
        "return count");
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask<bool> TryAcquireAsync(
        string partition,
        string key,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (permitLimit < 1 || window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(permitLimit));
        }

        var normalizedPartition = partition.Trim().ToLowerInvariant();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var count = (long)await database.ScriptEvaluateAsync(
            IncrementScript,
            new
            {
                key = (RedisKey)$"keywars:limit:{normalizedPartition}:{digest}",
                windowMilliseconds = Math.Max(1L, (long)window.TotalMilliseconds)
            });
        return count <= permitLimit;
    }
}
