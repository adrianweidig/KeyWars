using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class ErfolgeModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public IReadOnlyList<Achievement> Achievements { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Achievements = (await db.Achievements.Where(item => item.UserProfileId == profile.Id).ToListAsync(cancellationToken))
            .OrderByDescending(item => item.UnlockedAt)
            .ToList();
    }
}
