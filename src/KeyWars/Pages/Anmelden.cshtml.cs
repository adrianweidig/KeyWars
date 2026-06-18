using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using KeyWars.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages;

[AllowAnonymous]
public sealed class AnmeldenModel(ILdapAuthenticator authenticator, ProfileProvisioner provisioner) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await authenticator.AuthenticateAsync(Input.Username, Input.Password, cancellationToken);
        if (!result.Succeeded || result.Identity is null)
        {
            ModelState.AddModelError(string.Empty, "Anmeldung fehlgeschlagen. Bitte prüfe Benutzername und Passwort.");
            return Page();
        }

        var profile = await provisioner.ProvisionAsync(result.Identity, cancellationToken);
        var claims = new List<Claim>
        {
            new(KeyWarsClaims.ProfileId, profile.Id.ToString("D")),
            new(ClaimTypes.NameIdentifier, profile.Id.ToString("D")),
            new(ClaimTypes.Name, profile.DisplayName),
            new("samAccountName", profile.SamAccountName)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect("/");
    }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Der Benutzername ist erforderlich.")]
        [MaxLength(256)]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Das Passwort ist erforderlich.")]
        [DataType(DataType.Password)]
        [MaxLength(512)]
        public string Password { get; set; } = "";
    }
}
