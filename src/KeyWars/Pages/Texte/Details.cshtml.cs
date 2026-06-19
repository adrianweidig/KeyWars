using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class DetailsModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public TrainingText Text { get; private set; } = new();
    public TextQuality Quality { get; private set; } = new(0, 0, 0, 0, 0, 0, false);
    public bool CanManage { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCopyAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var copy = await texts.CopyAsync(profile.Id, id, cancellationToken);
        return RedirectToPage("/Texte/Details", new { id = copy.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            await texts.DeleteAsync(profile.Id, id, cancellationToken);
            return RedirectToPage("/Texte/Index");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(id, cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Text = await texts.GetVisibleAsync(profile.Id, id, cancellationToken);
        CanManage = Text.OwnerProfileId == profile.Id && !Text.IsStandard;
        Quality = texts.AnalyzeText(Text.Body);
    }
}
