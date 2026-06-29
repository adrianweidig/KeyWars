using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class ErfolgeModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public IReadOnlyList<AchievementCard> Achievements { get; private set; } = [];
    public int UnlockedCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var unlocked = (await db.Achievements.Where(item => item.UserProfileId == profile.Id).ToListAsync(cancellationToken))
            .ToDictionary(item => item.Key, StringComparer.Ordinal);
        UnlockedCount = unlocked.Count;
        Achievements = MotivationCatalog.AchievementDefinitions
            .Select(definition =>
            {
                unlocked.TryGetValue(definition.Key, out var achievement);
                var visual = MotivationVisuals.ForAchievementKey(definition.Key);
                return new AchievementCard(
                    definition.Key,
                    definition.Category,
                    definition.Title,
                    definition.Description,
                    achievement?.UnlockedAt,
                    visual.VisualKey,
                    visual.Accent);
            })
            .OrderBy(item => item.Unlocked ? 0 : 1)
            .ThenByDescending(item => item.UnlockedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed record AchievementCard(
    string Key,
    string Category,
    string Title,
    string Description,
    DateTimeOffset? UnlockedAt,
    string VisualKey,
    string Accent)
{
    public bool Unlocked => UnlockedAt is not null;
}
