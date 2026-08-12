namespace KeyWars.Services;

public interface IChallengeLockProvider
{
    ValueTask<IOperationLease> AcquireAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default);
}

public sealed class LocalChallengeLockProvider : IChallengeLockProvider
{
    public static LocalChallengeLockProvider Shared { get; } = new();

    private readonly AsyncKeyedLock<Guid> locks = new();

    public async ValueTask<IOperationLease> AcquireAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default) =>
        new LocalOperationLease(await locks.AcquireAsync(challengeId, cancellationToken));

    private sealed class LocalOperationLease(IAsyncDisposable lease) : IOperationLease
    {
        public CancellationToken LeaseLost => CancellationToken.None;
        public void ThrowIfLost()
        {
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
