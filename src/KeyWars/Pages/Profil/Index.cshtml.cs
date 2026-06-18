using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class IndexModel(CurrentUser currentUser, KeyWarsDbContext db, ProfilePrivacyService privacy) : PageModel
{
    public UserProfile Profile { get; private set; } = new();
    public IReadOnlyList<TypingAttempt> Attempts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Attempts = (await db.TypingAttempts.Where(item => item.UserProfileId == Profile.Id)
            .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .Take(10)
            .ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await privacy.DeleteProfileAsync(profile.Id, cancellationToken);
        return RedirectToPage("/Abmelden");
    }
}
