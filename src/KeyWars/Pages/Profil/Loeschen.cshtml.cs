using KeyWars.Auth;
using KeyWars.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class LoeschenModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        profile.Deleted = true;
        profile.DisplayName = "Gelöschtes Profil";
        profile.Email = null;
        profile.GivenName = null;
        profile.Surname = null;
        profile.Department = null;
        profile.Title = null;
        await db.SaveChangesAsync(cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/anmelden");
    }
}
