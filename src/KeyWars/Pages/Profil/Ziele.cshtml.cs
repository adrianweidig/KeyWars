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
        await motivation.EnsureDailyMissionsAsync(profile.Id, today, cancellationToken);
        Missions = await db.Missions.Where(item => item.UserProfileId == profile.Id && item.MissionDate == today).ToListAsync(cancellationToken);
    }
}
