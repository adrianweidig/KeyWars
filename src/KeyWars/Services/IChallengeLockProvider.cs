namespace KeyWars.Services;

public interface IChallengeLockProvider
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default);
}

public sealed class LocalChallengeLockProvider : IChallengeLockProvider
{
    public static LocalChallengeLockProvider Shared { get; } = new();

    private readonly AsyncKeyedLock<Guid> locks = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default) =>
        locks.AcquireAsync(challengeId, cancellationToken);
}
