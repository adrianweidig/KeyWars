using KeyWars.Services;

namespace KeyWars.Data;

public interface IMaintenanceLease
{
    ValueTask<IOperationLease?> TryAcquireAsync(
        string operation,
        CancellationToken cancellationToken = default);
}

public sealed class SingleNodeMaintenanceLease : IMaintenanceLease
{
    public ValueTask<IOperationLease?> TryAcquireAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IOperationLease?>(NoopLease.Instance);
    }

    private sealed class NoopLease : IOperationLease
    {
        public static NoopLease Instance { get; } = new();
        public CancellationToken LeaseLost => CancellationToken.None;
        public void ThrowIfLost()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
