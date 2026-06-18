using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class IndexModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
    }
}
