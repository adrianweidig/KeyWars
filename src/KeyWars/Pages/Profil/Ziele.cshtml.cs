using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class ZieleModel(CurrentUser currentUser, KeyWarsDbContext db, MotivationService motivation, TimeProvider timeProvider) : PageModel
{
    public IReadOnlyList<Mission> Missions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var weekStart = MotivationService.GetWeekStart(today);
        await motivation.EnsureCurrentMissionsAsync(profile.Id, today, cancellationToken);
        Missions = await db.Missions
            .Where(item => item.UserProfileId == profile.Id && (item.MissionDate == today || item.MissionDate == weekStart))
            .OrderBy(item => item.MissionDate == today ? 0 : 1)
            .ThenBy(item => item.Title)
            .ToListAsync(cancellationToken);
    }
}
