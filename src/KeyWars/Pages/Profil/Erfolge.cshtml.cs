using KeyWars.Auth;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class ErfolgeModel(CurrentUser currentUser, AchievementProgressService achievementProgress) : PageModel
{
    public IReadOnlyList<AchievementCard> Achievements { get; private set; } = [];
    public int UnlockedCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var overview = await achievementProgress.ReadAsync(profile.Id, cancellationToken);
        UnlockedCount = overview.UnlockedCount;
        Achievements = MotivationCatalog.AchievementDefinitions
            .Select(definition =>
            {
                var progress = overview.Items[definition.Key];
                var visual = MotivationVisuals.ForAchievementKey(definition.Key);
                return new AchievementCard(
                    definition.Key,
                    definition.Category,
                    definition.Title,
                    definition.Description,
                    progress.UnlockedAt,
                    visual.VisualKey,
                    visual.Accent,
                    progress.Current,
                    progress.Target,
                    progress.Unit);
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
    string Accent,
    double Current,
    double Target,
    string Unit)
{
    public bool Unlocked => UnlockedAt is not null;
}
