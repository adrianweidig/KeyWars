using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages;

[AllowAnonymous]
public sealed class AbmeldenModel : PageModel
{
    public bool SignedOut { get; private set; }

    public void OnGet()
    {
        SignedOut = Request.Query.ContainsKey("abgemeldet") || User.Identity?.IsAuthenticated != true;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/abmelden?abgemeldet=1");
    }
}
