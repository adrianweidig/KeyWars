namespace KeyWars.Data;

public interface IMaintenanceLease
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string operation,
        CancellationToken cancellationToken = default);
}

public sealed class SingleNodeMaintenanceLease : IMaintenanceLease
{
    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable?>(NoopLease.Instance);
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
