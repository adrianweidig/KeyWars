using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class SingleNodeSharedRateLimiterTests
{
    [Fact]
    public async Task FixedWindowRejectsAfterLimitAndResetsAfterExpiry()
    {
        var clock = new ManualTimeProvider();
        var limiter = new SingleNodeSharedRateLimiter(clock);

        Assert.True(await limiter.TryAcquireAsync(" Upload ", "profile", 2, TimeSpan.FromMinutes(1)));
        Assert.True(await limiter.TryAcquireAsync("upload", "profile", 2, TimeSpan.FromMinutes(1)));
        Assert.False(await limiter.TryAcquireAsync("UPLOAD", "profile", 2, TimeSpan.FromMinutes(1)));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await limiter.TryAcquireAsync("upload", "profile", 2, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task CapacityFailsClosedUntilIdleWindowsExpire()
    {
        var clock = new ManualTimeProvider();
        var limiter = new SingleNodeSharedRateLimiter(clock, capacity: 2);

        Assert.True(await limiter.TryAcquireAsync("upload", "first", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await limiter.TryAcquireAsync("upload", "second", 1, TimeSpan.FromMinutes(1)));
        Assert.False(await limiter.TryAcquireAsync("upload", "third", 1, TimeSpan.FromMinutes(1)));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await limiter.TryAcquireAsync("upload", "third", 1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ConcurrentAcquisitionNeverExceedsPermitLimit()
    {
        var limiter = new SingleNodeSharedRateLimiter();
        var accepted = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (limiter.TryAcquireAsync("hub", "profile", 50, TimeSpan.FromMinutes(1))
                .AsTask().GetAwaiter().GetResult())
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.Equal(50, accepted);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
