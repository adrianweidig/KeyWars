using System.Collections.Concurrent;

namespace KeyWars.Services;

internal sealed class AsyncKeyedLock<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> entries = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        while (true)
        {
            entry = entries.GetOrAdd(key, static _ => new Entry());
            lock (entry.Gate)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount++;
                break;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseReference(TKey key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        lock (entry.Gate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount != 0)
            {
                return;
            }

            entry.Retired = true;
            entries.TryRemove(KeyValuePair.Create(key, entry));
        }
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class Lease(AsyncKeyedLock<TKey> owner, TKey key, Entry entry) : IAsyncDisposable
    {
        private AsyncKeyedLock<TKey>? owner = owner;

        public ValueTask DisposeAsync()
        {
            var currentOwner = Interlocked.Exchange(ref owner, null);
            currentOwner?.ReleaseReference(key, entry, releaseSemaphore: true);
            return ValueTask.CompletedTask;
        }
    }
}
