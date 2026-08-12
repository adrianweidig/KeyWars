using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record GamificationEventDraft(
    GamificationEventType Type,
    string EventKey,
    string Title,
    string Description,
    int XpDelta,
    int LevelBefore,
    int LevelAfter,
    GamificationRarity Rarity,
    string Source,
    string SourceId);

internal readonly record struct GamificationEventIdentity(
    Guid UserProfileId,
    string Source,
    string SourceId,
    string EventKey);

public sealed class GamificationEventWriter(KeyWarsDbContext db)
{
    // Events are a private presentation feed. XP authority stays in RewardLedgerEntry,
    // so this writer must never be used as the source of truth for balances.
    public async Task AddAsync(
        ICollection<GamificationEvent> createdEvents,
        UserProfile profile,
        GamificationEventDraft draft,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(profile.Id, draft);
        var localExists = db.GamificationEvents.Local.Any(item =>
            item.UserProfileId == identity.UserProfileId &&
            item.Source == identity.Source &&
            item.SourceId == identity.SourceId &&
            item.EventKey == identity.EventKey);
        var exists = localExists || await db.GamificationEvents.AnyAsync(item =>
            item.UserProfileId == identity.UserProfileId &&
            item.Source == identity.Source &&
            item.SourceId == identity.SourceId &&
            item.EventKey == identity.EventKey,
            cancellationToken);
        if (exists)
        {
            return;
        }

        AddCore(createdEvents, profile, draft, createdAt, identity);
    }

    internal void AddPrepared(
        ICollection<GamificationEvent> createdEvents,
        UserProfile profile,
        GamificationEventDraft draft,
        DateTimeOffset createdAt,
        ISet<GamificationEventIdentity> knownEvents)
    {
        var identity = CreateIdentity(profile.Id, draft);
        if (!knownEvents.Add(identity))
        {
            return;
        }

        AddCore(createdEvents, profile, draft, createdAt, identity);
    }

    internal static GamificationEventIdentity CreateIdentity(Guid profileId, GamificationEventDraft draft) =>
        new(
            profileId,
            Normalize(draft.Source, 64),
            NormalizeSourceId(draft.SourceId),
            Normalize(draft.EventKey, 80));

    internal static string NormalizeSourceId(string sourceId) => Normalize(sourceId, 80);

    private void AddCore(
        ICollection<GamificationEvent> createdEvents,
        UserProfile profile,
        GamificationEventDraft draft,
        DateTimeOffset createdAt,
        GamificationEventIdentity identity)
    {
        var gamificationEvent = new GamificationEvent
        {
            UserProfileId = profile.Id,
            Type = draft.Type,
            EventKey = identity.EventKey,
            Title = Normalize(draft.Title, 160),
            Description = Normalize(draft.Description, 360),
            XpDelta = draft.XpDelta,
            LevelBefore = draft.LevelBefore,
            LevelAfter = draft.LevelAfter,
            Rarity = draft.Rarity,
            Source = identity.Source,
            SourceId = identity.SourceId,
            CreatedAt = createdAt
        };
        db.GamificationEvents.Add(gamificationEvent);
        createdEvents.Add(gamificationEvent);
    }

    private static string Normalize(string value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
