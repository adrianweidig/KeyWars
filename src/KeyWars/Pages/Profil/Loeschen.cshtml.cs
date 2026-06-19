using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class LoeschenModel(CurrentUser currentUser, ProfilePrivacyService privacy) : PageModel
{
    [BindProperty]
    public ConfirmationInput Input { get; set; } = new();

    public string ConfirmationName { get; private set; } = "-";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        PopulateConfirmation(profile);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        PopulateConfirmation(profile);
        if (!MatchesConfirmation(profile))
        {
            ModelState.AddModelError("Input.Confirmation", $"Gib {profile.SamAccountName} ein, um dein Profil zu löschen.");
            return Page();
        }

        await privacy.DeleteProfileAsync(profile.Id, cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/anmelden");
    }

    private void PopulateConfirmation(UserProfile profile)
    {
        ConfirmationName = profile.SamAccountName;
    }

    private bool MatchesConfirmation(UserProfile profile) =>
        string.Equals(Input.Confirmation?.Trim(), profile.SamAccountName, StringComparison.OrdinalIgnoreCase);

    public sealed class ConfirmationInput
    {
        public string? Confirmation { get; set; }
    }
}
