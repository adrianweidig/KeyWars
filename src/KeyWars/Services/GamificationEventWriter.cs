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
        var normalizedSource = Normalize(draft.Source, 64);
        var normalizedSourceId = Normalize(draft.SourceId, 80);
        var normalizedEventKey = Normalize(draft.EventKey, 80);
        var localExists = db.GamificationEvents.Local.Any(item =>
            item.UserProfileId == profile.Id &&
            item.Source == normalizedSource &&
            item.SourceId == normalizedSourceId &&
            item.EventKey == normalizedEventKey);
        var exists = localExists || await db.GamificationEvents.AnyAsync(item =>
            item.UserProfileId == profile.Id &&
            item.Source == normalizedSource &&
            item.SourceId == normalizedSourceId &&
            item.EventKey == normalizedEventKey,
            cancellationToken);
        if (exists)
        {
            return;
        }

        var gamificationEvent = new GamificationEvent
        {
            UserProfileId = profile.Id,
            Type = draft.Type,
            EventKey = normalizedEventKey,
            Title = Normalize(draft.Title, 160),
            Description = Normalize(draft.Description, 360),
            XpDelta = draft.XpDelta,
            LevelBefore = draft.LevelBefore,
            LevelAfter = draft.LevelAfter,
            Rarity = draft.Rarity,
            Source = normalizedSource,
            SourceId = normalizedSourceId,
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
