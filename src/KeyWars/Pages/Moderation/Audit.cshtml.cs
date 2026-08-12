using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Moderation;

[Authorize(Policy = KeyWarsPolicies.ContentModerator)]
public sealed class AuditModel(ContentModerationService moderation) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Seite { get; set; } = 1;

    public ContentModerationAuditPage Audit { get; private set; } = new([], 0, 1, 50, 1);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Audit = await moderation.GetAuditAsync(User, Seite, 50, cancellationToken);
        Seite = Audit.Page;
    }
}
