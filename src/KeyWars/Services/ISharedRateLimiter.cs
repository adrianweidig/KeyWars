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

public sealed class SingleNodeSharedRateLimiter : ISharedRateLimiter
{
    public ValueTask<bool> TryAcquireAsync(
        string partition,
        string key,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }
}
