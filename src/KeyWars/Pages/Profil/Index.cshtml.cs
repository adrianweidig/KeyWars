using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;

namespace KeyWars.Pages.Profil;

public sealed class IndexModel(CurrentUser currentUser, ProfileInsightsService insights) : PageModel
{
    public UserProfile Profile { get; private set; } = new();
    public ProfileInsights Insights { get; private set; } = EmptyInsights;
    public LevelProgress LevelProgress { get; private set; } = new(1, 0, 0, 200, 0, 200, 0);
    public ProfileInsightsPeriod SelectedPeriod { get; private set; } = ProfileInsightsPeriod.NinetyDays;
    public int SelectedDays => (int)SelectedPeriod;
    public IReadOnlyList<ProfileInsightsPeriod> AvailablePeriods { get; } =
        [ProfileInsightsPeriod.SevenDays, ProfileInsightsPeriod.ThirtyDays, ProfileInsightsPeriod.NinetyDays];

    [BindProperty(SupportsGet = true)]
    public int Seite { get; set; } = 1;
    [BindProperty(SupportsGet = true)]
    public string? Zeitraum { get; set; } = "90";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        LevelProgress = MotivationService.GetLevelProgress(Profile.ExperiencePoints);
        int? requestedDays = int.TryParse(Zeitraum, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            ? days
            : null;
        var query = ProfileInsightsQuery.FromDays(requestedDays, Seite, 10);
        SelectedPeriod = query.Period;
        Zeitraum = query.Days.ToString(CultureInfo.InvariantCulture);
        Insights = await insights.GetAsync(Profile, query, cancellationToken);
        Seite = Insights.HistoryPage;
    }

    public IActionResult OnPostDelete() => RedirectToPage("/Profil/Loeschen");

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
        [],
        []);
}
