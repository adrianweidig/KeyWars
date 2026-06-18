using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Herausforderungen;

public sealed class IndexModel(CurrentUser currentUser, ChallengeService challenges) : PageModel
{
    public IReadOnlyList<Challenge> Challenges { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Challenges = await challenges.ListForProfileAsync(profile.Id, cancellationToken);
    }
}
