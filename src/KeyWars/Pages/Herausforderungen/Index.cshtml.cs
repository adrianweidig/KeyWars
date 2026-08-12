using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Herausforderungen;

public sealed class IndexModel(CurrentUser currentUser, ChallengeService challenges) : PageModel
{
    public ChallengeListPage ChallengePage { get; private set; } =
        new([], ChallengeListFilter.All, 1, 20, 0, 1, 0);

    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "seite")]
    public int Seite { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        ChallengePage = await challenges.ListPageForProfileAsync(
            profile.Id,
            ParseFilter(Status),
            Seite,
            20,
            cancellationToken);
        Seite = ChallengePage.Page;
        Status = FilterValue(ChallengePage.Filter);
    }

    public static string FilterValue(ChallengeListFilter filter) => filter switch
    {
        ChallengeListFilter.Invitations => "einladungen",
        ChallengeListFilter.Active => "aktiv",
        ChallengeListFilter.Completed => "abgeschlossen",
        _ => "alle"
    };

    private static ChallengeListFilter ParseFilter(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "einladungen" => ChallengeListFilter.Invitations,
        "aktiv" => ChallengeListFilter.Active,
        "abgeschlossen" => ChallengeListFilter.Completed,
        _ => ChallengeListFilter.All
    };
}
