using KeyWars.Auth;
using KeyWars.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class StatistikZuruecksetzenModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await db.TypingAttempts.Where(item => item.UserProfileId == profile.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Missions.Where(item => item.UserProfileId == profile.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Achievements.Where(item => item.UserProfileId == profile.Id).ExecuteDeleteAsync(cancellationToken);
        await db.WeaknessObservations.Where(item => item.UserProfileId == profile.Id).ExecuteDeleteAsync(cancellationToken);
        profile.ExperiencePoints = 0;
        profile.Level = 1;
        profile.SeasonPoints = 0;
        profile.CurrentStreakDays = 0;
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("/Profil/Index");
    }
}
