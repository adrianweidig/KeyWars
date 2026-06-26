using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class IndexModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
    }

    public string Preview(TrainingText text)
    {
        var normalized = string.Join(
            " ",
            text.Body.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 138 ? normalized : $"{normalized[..138]}...";
    }
}
