using System.Collections.Concurrent;

namespace KeyWars.Services;

public enum ProfileAccessState
{
    Available,
    OperationInProgress,
    Deleted
}

public interface IProfileAccessGate
{
    ValueTask<ProfileAccessState> GetStateAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireManyAsync(
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken = default);
    ValueTask<bool> TryBeginOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
    Task WaitForIdleAsync(Guid profileId, CancellationToken cancellationToken = default);
    ValueTask CompleteOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask MarkDeletedAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileAccessGate : IProfileAccessGate
{
    private readonly ConcurrentDictionary<Guid, ProfileAccessEntry> entries = new();

    public ProfileAccessState GetState(Guid profileId)
    {
        if (!entries.TryGetValue(profileId, out var entry))
        {
            return ProfileAccessState.Available;
        }

        lock (entry.Gate)
        {
            return entry.State;
        }
    }

    public bool IsBlocked(Guid profileId) => GetState(profileId) != ProfileAccessState.Available;

    public ValueTask<ProfileAccessState> GetStateAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetState(profileId));
    }

    public IDisposable Acquire(Guid profileId)
    {
        if (!TryAcquire(profileId, out var lease))
        {
            throw CreateBlockedException(profileId);
        }

        return lease!;
    }

    public bool TryAcquire(Guid profileId, out IDisposable? lease)
    {
        var entry = entries.GetOrAdd(profileId, static _ => new ProfileAccessEntry());
        lock (entry.Gate)
        {
            if (entry.State != ProfileAccessState.Available)
            {
                lease = null;
                return false;
            }

            entry.ActiveOperations++;
            lease = new ProfileAccessLease(entry);
            return true;
        }
    }

    public IDisposable AcquireMany(IEnumerable<Guid> profileIds)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var profileId in profileIds.Distinct().Order())
            {
                leases.Add(Acquire(profileId));
            }

            return new CompositeProfileAccessLease(leases);
        }
        catch
        {
            DisposeReverse(leases);
            throw;
        }
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(new AsyncLease(Acquire(profileId)));
    }

    public ValueTask<IAsyncDisposable> AcquireManyAsync(
        IEnumerable<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(new AsyncLease(AcquireMany(profileIds)));
    }

    public bool TryBeginOperation(Guid profileId)
    {
        var entry = entries.GetOrAdd(profileId, static _ => new ProfileAccessEntry());
        lock (entry.Gate)
        {
            if (entry.State != ProfileAccessState.Available)
            {
                return false;
            }

            entry.State = ProfileAccessState.OperationInProgress;
            entry.Idle = entry.ActiveOperations == 0
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public ValueTask<bool> TryBeginOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryBeginOperation(profileId));
    }

    public Task WaitForIdleAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var entry = entries.GetOrAdd(profileId, static _ => new ProfileAccessEntry());
        Task waitTask;
        lock (entry.Gate)
        {
            if (entry.State != ProfileAccessState.OperationInProgress)
            {
                throw new InvalidOperationException("Für dieses Profil wurde keine exklusive Operation begonnen.");
            }

            if (entry.ActiveOperations == 0)
            {
                return Task.CompletedTask;
            }

            entry.Idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = entry.Idle.Task;
        }

        return waitTask.WaitAsync(cancellationToken);
    }

    public void CompleteOperation(Guid profileId)
    {
        if (!entries.TryGetValue(profileId, out var entry))
        {
            return;
        }

        lock (entry.Gate)
        {
            if (entry.State != ProfileAccessState.OperationInProgress)
            {
                return;
            }

            entry.State = ProfileAccessState.Available;
            entry.Generation++;
            entry.Idle = null;
        }
    }

    public ValueTask CompleteOperationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompleteOperation(profileId);
        return ValueTask.CompletedTask;
    }

    public void MarkDeleted(Guid profileId)
    {
        var entry = entries.GetOrAdd(profileId, static _ => new ProfileAccessEntry());
        lock (entry.Gate)
        {
            if (entry.ActiveOperations != 0)
            {
                throw new InvalidOperationException("Ein Profil kann erst nach Abschluss aller laufenden Operationen gelöscht werden.");
            }

            entry.State = ProfileAccessState.Deleted;
            entry.Generation++;
            entry.Idle = null;
        }
    }

    public ValueTask MarkDeletedAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkDeleted(profileId);
        return ValueTask.CompletedTask;
    }

    private ProfileOperationException CreateBlockedException(Guid profileId) =>
        GetState(profileId) == ProfileAccessState.Deleted
            ? new ProfileOperationException("profile_deleted", "Dieses Profil wurde bereits gelöscht.")
            : new ProfileOperationException("profile_operation_in_progress", "Für dieses Profil läuft bereits eine Datenschutzoperation.");

    private static void DisposeReverse(IReadOnlyList<IDisposable> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            leases[index].Dispose();
        }
    }

    private sealed class ProfileAccessEntry
    {
        public object Gate { get; } = new();
        public ProfileAccessState State { get; set; }
        public int ActiveOperations { get; set; }
        public long Generation { get; set; }
        public TaskCompletionSource? Idle { get; set; }
    }

    private sealed class ProfileAccessLease(ProfileAccessEntry entry) : IDisposable
    {
        private ProfileAccessEntry? current = entry;

        public void Dispose()
        {
            var released = Interlocked.Exchange(ref current, null);
            if (released is null)
            {
                return;
            }

            TaskCompletionSource? idle = null;
            lock (released.Gate)
            {
                released.ActiveOperations--;
                if (released.ActiveOperations < 0)
                {
                    throw new InvalidOperationException("Der Profilzugriffszähler ist inkonsistent.");
                }

                if (released.ActiveOperations == 0 && released.State == ProfileAccessState.OperationInProgress)
                {
                    idle = released.Idle;
                }
            }

            idle?.TrySetResult();
        }
    }

    private sealed class CompositeProfileAccessLease(List<IDisposable> leases) : IDisposable
    {
        private List<IDisposable>? current = leases;

        public void Dispose()
        {
            var released = Interlocked.Exchange(ref current, null);
            if (released is not null)
            {
                DisposeReverse(released);
            }
        }
    }

    private sealed class AsyncLease(IDisposable lease) : IAsyncDisposable
    {
        private IDisposable? current = lease;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref current, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ProfileOperationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
