using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class KopierenModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public Guid NewId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var original = await texts.GetVisibleAsync(profile.Id, id, cancellationToken);
        var copy = await texts.CreateAsync(profile.Id, $"{original.Title} Kopie", original.Body, Domain.TrainingTextVisibility.Private, cancellationToken);
        NewId = copy.Id;
        return Page();
    }
}
