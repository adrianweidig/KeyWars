namespace KeyWars.Services;

public interface IProfileExportConcurrencyGate
{
    ValueTask<IOperationLease?> TryAcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}

public sealed class SingleNodeProfileExportConcurrencyGate(
    int capacity = SingleNodeProfileExportConcurrencyGate.DefaultCapacity) : IProfileExportConcurrencyGate
{
    public const int DefaultCapacity = 256;

    private readonly int entryCapacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object sync = new();
    private readonly HashSet<Guid> activeProfiles = [];

    public ValueTask<IOperationLease?> TryAcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Das Exportprofil darf nicht leer sein.", nameof(profileId));
        }

        lock (sync)
        {
            if (activeProfiles.Count >= entryCapacity || !activeProfiles.Add(profileId))
            {
                return ValueTask.FromResult<IOperationLease?>(null);
            }

            return ValueTask.FromResult<IOperationLease?>(new Lease(this, profileId));
        }
    }

    private void Release(Guid profileId)
    {
        lock (sync)
        {
            activeProfiles.Remove(profileId);
        }
    }

    private sealed class Lease(
        SingleNodeProfileExportConcurrencyGate owner,
        Guid profileId) : IOperationLease
    {
        private int disposed;

        public CancellationToken LeaseLost => CancellationToken.None;

        public void ThrowIfLost()
        {
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(profileId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
