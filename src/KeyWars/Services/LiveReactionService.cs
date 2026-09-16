using System.Collections.Concurrent;

namespace KeyWars.Services;

public sealed record LiveReactionSnapshot(
    Guid RoomId,
    Guid ProfileId,
    string DisplayName,
    string Key,
    string Label,
    DateTimeOffset SentAt,
    int SuppressedCount);

public sealed class LiveReactionService : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private const int MaxReactionsPerWindow = 5;
    private const int MaxCleanupBatch = 512;
    internal const int DefaultMaxTrackedStates = 100_000;

    private static readonly IReadOnlyDictionary<string, string> AllowedReactions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["stark"] = "Stark",
        ["knapp"] = "Knapp",
        ["sauber"] = "Sauber",
        ["revanche"] = "Revanche",
        ["respekt"] = "Respekt"
    };

    private readonly TimeProvider timeProvider;
    private readonly int maxTrackedStates;
    private readonly ConcurrentDictionary<ReactionStateKey, ReactionRateState> states = new();
    private readonly object expirationGate = new();
    private readonly PriorityQueue<ReactionStateKey, long> expirations = new();
    private readonly ITimer cleanupTimer;
    private long nextCleanupAtTicks;
    private int cleanupRunning;
    private int trackedStateCount;
    private int disposed;

    public LiveReactionService(TimeProvider timeProvider)
        : this(timeProvider, DefaultMaxTrackedStates)
    {
    }

    internal LiveReactionService(TimeProvider timeProvider, int maxTrackedStates)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maxTrackedStates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrackedStates));
        }

        this.timeProvider = timeProvider;
        this.maxTrackedStates = maxTrackedStates;
        nextCleanupAtTicks = timeProvider.GetUtcNow().Add(CleanupInterval).UtcTicks;
        cleanupTimer = timeProvider.CreateTimer(
            static state => ((LiveReactionService)state!).SweepExpired(),
            this,
            CleanupInterval,
            CleanupInterval);
    }

    public IReadOnlyDictionary<string, string> Reactions => AllowedReactions;

    public LiveReactionSnapshot? TrySubmit(Guid roomId, Guid profileId, string displayName, string key)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var normalizedKey = NormalizeKey(key);
        if (!AllowedReactions.TryGetValue(normalizedKey, out var label))
        {
            throw new InvalidOperationException("Diese Reaktion ist nicht erlaubt.");
        }

        var now = timeProvider.GetUtcNow();
        TrySweepExpired(now);
        var stateKey = new ReactionStateKey(roomId, profileId);
        while (true)
        {
            if (!states.TryGetValue(stateKey, out var state) &&
                !TryAddState(stateKey, now, out state))
            {
                return null;
            }

            lock (state.Gate)
            {
                if (state.Retired)
                {
                    continue;
                }

                state.LastSeenAt = now;
                state.AllowedAt.RemoveAll(item => now - item > Window);
                if (now - state.LastAllowedAt < MinimumInterval || state.AllowedAt.Count >= MaxReactionsPerWindow)
                {
                    state.SuppressedCount += 1;
                    return null;
                }

                state.LastAllowedAt = now;
                state.AllowedAt.Add(now);
                var suppressed = state.SuppressedCount;
                state.SuppressedCount = 0;
                return new LiveReactionSnapshot(roomId, profileId, displayName, normalizedKey, label, now, suppressed);
            }
        }
    }

    private static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim().ToLowerInvariant();

    internal int TrackedStateCount => Volatile.Read(ref trackedStateCount);

    internal int ScheduledExpiryCount
    {
        get
        {
            lock (expirationGate)
            {
                return expirations.Count;
            }
        }
    }

    internal void SweepExpired() => SweepExpired(timeProvider.GetUtcNow(), force: true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            cleanupTimer.Dispose();
        }
    }

    private bool TryAddState(
        ReactionStateKey key,
        DateTimeOffset now,
        out ReactionRateState state)
    {
        while (true)
        {
            if (states.TryGetValue(key, out state!))
            {
                return true;
            }

            if (!TryReserveStateSlot())
            {
                SweepExpired(now, force: true);
                if (!TryReserveStateSlot())
                {
                    state = null!;
                    return false;
                }
            }

            var candidate = new ReactionRateState { LastSeenAt = now };
            if (states.TryAdd(key, candidate))
            {
                ScheduleExpiry(key, now.Add(CleanupAge));
                state = candidate;
                return true;
            }

            Interlocked.Decrement(ref trackedStateCount);
        }
    }

    private bool TryReserveStateSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref trackedStateCount);
            if (current >= maxTrackedStates)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref trackedStateCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void TrySweepExpired(DateTimeOffset now)
    {
        if (now.UtcTicks >= Volatile.Read(ref nextCleanupAtTicks))
        {
            SweepExpired(now, force: false);
        }
    }

    private void SweepExpired(DateTimeOffset now, bool force)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            !force && now.UtcTicks < Volatile.Read(ref nextCleanupAtTicks) ||
            Interlocked.CompareExchange(ref cleanupRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var processed = 0;
            while (processed < MaxCleanupBatch && TryTakeDueExpiry(now.UtcTicks, out var key))
            {
                processed++;
                if (!states.TryGetValue(key, out var state))
                {
                    continue;
                }

                DateTimeOffset? nextExpiry = null;
                lock (state.Gate)
                {
                    if (state.Retired)
                    {
                        continue;
                    }

                    if (now - state.LastSeenAt >= CleanupAge)
                    {
                        state.Retired = true;
                        if (states.TryRemove(KeyValuePair.Create(key, state)))
                        {
                            Interlocked.Decrement(ref trackedStateCount);
                        }
                    }
                    else
                    {
                        nextExpiry = state.LastSeenAt.Add(CleanupAge);
                    }
                }

                if (nextExpiry is { } dueAt)
                {
                    ScheduleExpiry(key, dueAt);
                }
            }

            Volatile.Write(
                ref nextCleanupAtTicks,
                HasDueExpiry(now.UtcTicks)
                    ? now.UtcTicks
                    : now.Add(CleanupInterval).UtcTicks);
        }
        finally
        {
            Volatile.Write(ref cleanupRunning, 0);
        }
    }

    private void ScheduleExpiry(ReactionStateKey key, DateTimeOffset dueAt)
    {
        lock (expirationGate)
        {
            expirations.Enqueue(key, dueAt.UtcTicks);
        }
    }

    private bool TryTakeDueExpiry(long nowTicks, out ReactionStateKey key)
    {
        lock (expirationGate)
        {
            if (expirations.TryPeek(out _, out var dueAtTicks) && dueAtTicks <= nowTicks)
            {
                key = expirations.Dequeue();
                return true;
            }
        }

        key = default;
        return false;
    }

    private bool HasDueExpiry(long nowTicks)
    {
        lock (expirationGate)
        {
            return expirations.TryPeek(out _, out var dueAtTicks) && dueAtTicks <= nowTicks;
        }
    }

    private readonly record struct ReactionStateKey(Guid RoomId, Guid ProfileId);

    private sealed class ReactionRateState
    {
        public object Gate { get; } = new();
        public List<DateTimeOffset> AllowedAt { get; } = [];
        public DateTimeOffset LastAllowedAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.MinValue;
        public int SuppressedCount { get; set; }
        public bool Retired { get; set; }
    }
}
