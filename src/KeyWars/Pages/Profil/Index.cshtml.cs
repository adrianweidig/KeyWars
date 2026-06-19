using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class IndexModel(CurrentUser currentUser, ProfileInsightsService insights, ProfilePrivacyService privacy) : PageModel
{
    public UserProfile Profile { get; private set; } = new();
    public ProfileInsights Insights { get; private set; } = EmptyInsights;
    public LevelProgress LevelProgress { get; private set; } = new(1, 0, 0, 200, 0, 200, 0);
    [BindProperty(SupportsGet = true)]
    public int Seite { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        LevelProgress = MotivationService.GetLevelProgress(Profile.ExperiencePoints);
        Insights = await insights.GetAsync(Profile, Seite, 10, cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await privacy.DeleteProfileAsync(profile.Id, cancellationToken);
        return RedirectToPage("/Abmelden");
    }

    private static readonly ProfileInsights EmptyInsights = new(
        "KW",
        "Bronze",
        new ProfileTotals(0, 0, 0, 0, 0, TimeSpan.Zero),
        [],
        [],
        [],
        [],
        1,
        10,
        0,
        1,
        [],
        []);
}
