using System.Collections.Concurrent;

namespace KeyWars.Services;

public sealed class AttemptSessionStore : IAttemptSessionStateStore
{
    private readonly ConcurrentDictionary<Guid, AttemptSession> sessions = new();
    private readonly AsyncKeyedLock<Guid> lifecycleLocks = new();
    private readonly object sessionGate = new();

    public void Add(AttemptSession session)
    {
        lock (sessionGate)
        {
            sessions[session.Id] = session;
        }
    }

    public bool TryGet(Guid id, out AttemptSession? session)
    {
        lock (sessionGate)
        {
            return sessions.TryGetValue(id, out session);
        }
    }

    public bool TryUpdate(AttemptSession current, AttemptSession updated)
    {
        lock (sessionGate)
        {
            return sessions.TryUpdate(current.Id, updated, current);
        }
    }

    public bool TryRemove(Guid id, out AttemptSession? session)
    {
        lock (sessionGate)
        {
            return sessions.TryRemove(id, out session);
        }
    }

    public IReadOnlyList<AttemptSession> RemoveProfile(Guid profileId)
    {
        lock (sessionGate)
        {
            var removed = new List<AttemptSession>();
            foreach (var item in sessions.Where(item => item.Value.UserProfileId == profileId).ToArray())
            {
                if (sessions.TryRemove(item))
                {
                    removed.Add(item.Value);
                }
            }

            return removed;
        }
    }

    public ValueTask<IAsyncDisposable> AcquireLifecycleLockAsync(Guid id, CancellationToken cancellationToken = default) =>
        lifecycleLocks.AcquireAsync(id, cancellationToken);

    public IReadOnlyList<Guid> GetExpiredIds(DateTimeOffset now, TimeSpan lifetime)
    {
        lock (sessionGate)
        {
            return sessions
                .Where(item => IsExpired(item.Value, now, lifetime))
                .Select(item => item.Key)
                .ToArray();
        }
    }

    public bool TryRemoveExpired(Guid id, DateTimeOffset now, TimeSpan lifetime, out AttemptSession? session)
    {
        lock (sessionGate)
        {
            session = null;
            if (!sessions.TryGetValue(id, out var candidate) || !IsExpired(candidate, now, lifetime))
            {
                return false;
            }

            if (!sessions.TryRemove(KeyValuePair.Create(id, candidate)))
            {
                return false;
            }

            session = candidate;
            return true;
        }
    }

    public IReadOnlyList<AttemptSession> RemoveExpired(DateTimeOffset now, TimeSpan lifetime)
    {
        lock (sessionGate)
        {
            var expired = new List<AttemptSession>();
            foreach (var item in sessions)
            {
                var reference = item.Value.StartedAt ?? item.Value.PreparedAt;
                if (now - reference > lifetime && sessions.TryRemove(item.Key, out var session))
                {
                    expired.Add(session);
                }
            }

            return expired;
        }
    }

    private static bool IsExpired(AttemptSession session, DateTimeOffset now, TimeSpan lifetime)
    {
        var reference = session.StartedAt ?? session.PreparedAt;
        return now - reference > lifetime;
    }

    ValueTask IAttemptSessionStateStore.AddAsync(
        AttemptSession session,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Add(session);
        return ValueTask.CompletedTask;
    }

    ValueTask<AttemptSession?> IAttemptSessionStateStore.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryGet(id, out var session);
        return ValueTask.FromResult(session);
    }

    ValueTask<bool> IAttemptSessionStateStore.TryUpdateAsync(
        AttemptSession current,
        AttemptSession updated,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryUpdate(current, updated));
    }

    ValueTask<AttemptSession?> IAttemptSessionStateStore.RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryRemove(id, out var session);
        return ValueTask.FromResult(session);
    }

    ValueTask<IReadOnlyList<AttemptSession>> IAttemptSessionStateStore.RemoveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RemoveProfile(profileId));
    }

    ValueTask<IReadOnlyList<Guid>> IAttemptSessionStateStore.GetExpiredIdsAsync(
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetExpiredIds(now, lifetime));
    }

    ValueTask<AttemptSession?> IAttemptSessionStateStore.TryRemoveExpiredAsync(
        Guid id,
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryRemoveExpired(id, now, lifetime, out var session);
        return ValueTask.FromResult(session);
    }
}
