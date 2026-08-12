using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class IndexModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    private const int PageSize = 48;

    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public bool HasPreviousPage => Seite > 1;
    public bool HasNextPage => Seite < TotalPages;
    public int FirstVisibleNumber => TotalCount == 0 ? 0 : (int)(((long)Seite - 1) * PageSize + 1);
    public int LastVisibleNumber => (int)Math.Min((long)Seite * PageSize, TotalCount);

    [BindProperty(SupportsGet = true)]
    public string? Suche { get; set; }
    [BindProperty(SupportsGet = true)]
    public TrainingTextVisibility? Sichtbarkeit { get; set; }
    [BindProperty(SupportsGet = true)]
    public int Seite { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Suche = string.IsNullOrWhiteSpace(Suche) ? null : Suche.Trim();
        var result = await texts.GetVisiblePageAsync(
            profile.Id,
            Suche,
            Sichtbarkeit,
            Seite,
            PageSize,
            cancellationToken);
        Texts = result.Items;
        TotalCount = result.TotalCount;
        TotalPages = result.TotalPages;
        Seite = result.Page;
    }
}
