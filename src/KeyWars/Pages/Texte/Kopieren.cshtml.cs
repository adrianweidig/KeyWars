using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class KopierenModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public TrainingText Original { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Original = await texts.GetVisibleAsync(profile.Id, id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var copy = await texts.CopyAsync(profile.Id, id, cancellationToken);
        return RedirectToPage("/Texte/Details", new { id = copy.Id });
    }
}
