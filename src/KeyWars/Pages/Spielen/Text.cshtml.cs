using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class TextModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public TrainingText Text { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Text = await texts.GetVisibleAsync(profile.Id, id, cancellationToken);
        return Page();
    }
}
