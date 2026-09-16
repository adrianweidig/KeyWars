using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using KeyWars.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace KeyWars.Pages;

[AllowAnonymous]
public sealed class AnmeldenModel(
    ILdapAuthenticator authenticator,
    ProfileProvisioner provisioner,
    IOptions<ContentModerationOptions> moderationOptions,
    IOptions<AuthOptions> authOptions,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

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
        claims.AddRange(ContentModeratorClaims.Create(result.Identity, moderationOptions.Value));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var now = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var absoluteLifetime = ProfileCookieValidation.GetAbsoluteLifetime(authOptions.Value.CookieLifetimeHours);
        ProfileCookieValidation.Initialize(principal, now);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                IssuedUtc = now,
                ExpiresUtc = now.Add(absoluteLifetime)
            });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
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
