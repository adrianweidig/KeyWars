namespace KeyWars.Services;

public sealed record LiveReactionSnapshot(
    Guid RoomId,
    Guid ProfileId,
    string DisplayName,
    string Key,
    string Label,
    DateTimeOffset SentAt,
    int SuppressedCount);

public sealed class LiveReactionService(TimeProvider timeProvider)
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupAge = TimeSpan.FromMinutes(10);
    private const int MaxReactionsPerWindow = 5;

    private static readonly IReadOnlyDictionary<string, string> AllowedReactions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["stark"] = "Stark",
        ["knapp"] = "Knapp",
        ["sauber"] = "Sauber",
        ["revanche"] = "Revanche",
        ["respekt"] = "Respekt"
    };

    private readonly object gate = new();
    private readonly Dictionary<(Guid RoomId, Guid ProfileId), ReactionRateState> states = [];

    public IReadOnlyDictionary<string, string> Reactions => AllowedReactions;

    public LiveReactionSnapshot? TrySubmit(Guid roomId, Guid profileId, string displayName, string key)
    {
        var normalizedKey = NormalizeKey(key);
        if (!AllowedReactions.TryGetValue(normalizedKey, out var label))
        {
            throw new InvalidOperationException("Diese Reaktion ist nicht erlaubt.");
        }

        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            Cleanup(now);
            var stateKey = (roomId, profileId);
            if (!states.TryGetValue(stateKey, out var state))
            {
                state = new ReactionRateState();
                states[stateKey] = state;
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

    private static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim().ToLowerInvariant();

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var stale in states.Where(item => now - item.Value.LastSeenAt > CleanupAge).Select(item => item.Key).ToList())
        {
            states.Remove(stale);
        }
    }

    private sealed class ReactionRateState
    {
        public List<DateTimeOffset> AllowedAt { get; } = [];
        public DateTimeOffset LastAllowedAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.MinValue;
        public int SuppressedCount { get; set; }
    }
}
