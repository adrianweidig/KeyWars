using System.Security.Cryptography;
using System.Text;

namespace KeyWars.Services;

public interface ISharedRateLimiter
{
    ValueTask<bool> TryAcquireAsync(
        string partition,
        string key,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}

public sealed class SingleNodeSharedRateLimiter(
    TimeProvider? timeProvider = null,
    int capacity = SingleNodeSharedRateLimiter.DefaultCapacity) : ISharedRateLimiter
{
    public const int DefaultCapacity = 100_000;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly int entryCapacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object sync = new();
    private readonly Dictionary<string, Window> windows = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, long> expirations = new();

    public ValueTask<bool> TryAcquireAsync(
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
        var bucketKey = $"{normalizedPartition}:{digest}";
        var now = clock.GetUtcNow().UtcTicks;

        lock (sync)
        {
            EvictExpired(now);
            if (windows.TryGetValue(bucketKey, out var existing))
            {
                if (existing.Count < long.MaxValue)
                {
                    existing.Count++;
                }

                return ValueTask.FromResult(existing.Count <= permitLimit);
            }

            if (windows.Count >= entryCapacity)
            {
                return ValueTask.FromResult(false);
            }

            var expiresAt = now > long.MaxValue - window.Ticks
                ? long.MaxValue
                : now + window.Ticks;
            windows.Add(bucketKey, new Window(expiresAt));
            expirations.Enqueue(bucketKey, expiresAt);
            return ValueTask.FromResult(true);
        }
    }

    private void EvictExpired(long now)
    {
        while (expirations.TryPeek(out var bucketKey, out var expiresAt) && expiresAt <= now)
        {
            expirations.Dequeue();
            if (windows.TryGetValue(bucketKey, out var window) && window.ExpiresAt <= now)
            {
                windows.Remove(bucketKey);
            }
        }
    }

    private sealed class Window(long expiresAt)
    {
        public long Count { get; set; } = 1;
        public long ExpiresAt { get; } = expiresAt;
    }
}
