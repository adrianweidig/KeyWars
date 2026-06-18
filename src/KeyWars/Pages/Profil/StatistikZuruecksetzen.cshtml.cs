using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class StatistikZuruecksetzenModel(CurrentUser currentUser, ProfilePrivacyService privacy) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await privacy.ResetStatisticsAsync(profile.Id, cancellationToken);
        return RedirectToPage("/Profil/Index");
    }
}
