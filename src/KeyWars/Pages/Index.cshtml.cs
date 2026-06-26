using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages;

public sealed class IndexModel(
    CurrentUser currentUser,
    KeyWarsDbContext db,
    MotivationService motivation,
    ChallengeService challenges,
    ProfileInsightsService insights,
    TimeProvider timeProvider) : PageModel
{
    public UserProfile Profile { get; private set; } = new();
    public IReadOnlyList<Mission> Missions { get; private set; } = [];
    public IReadOnlyList<Challenge> Challenges { get; private set; } = [];
    public ProfileInsights Insights { get; private set; } = EmptyInsights;
    public CoachRecommendation Recommendation { get; private set; } = new("Starte mit einer ruhigen Runde.", TrainingMode.Sprint60, 1);
    public LevelProgress LevelProgress { get; private set; } = new(1, 0, 0, 200, 0, 200, 0);
    public string LastWpm { get; private set; } = "-";
    public string LastAccuracy { get; private set; } = "-";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var weekStart = MotivationService.GetWeekStart(today);
        await motivation.EnsureCurrentMissionsAsync(Profile.Id, today, cancellationToken);
        Missions = await db.Missions
            .Where(item => item.UserProfileId == Profile.Id && (item.MissionDate == today || item.MissionDate == weekStart))
            .OrderBy(item => item.MissionDate == today ? 0 : 1)
            .ThenBy(item => item.Title)
            .ToListAsync(cancellationToken);
        Challenges = await challenges.ListForProfileAsync(Profile.Id, cancellationToken);
        Recommendation = await motivation.RecommendAsync(Profile.Id, cancellationToken);
        LevelProgress = MotivationService.GetLevelProgress(Profile.ExperiencePoints);
        Insights = await insights.GetAsync(Profile, 1, 5, cancellationToken);

        var culture = CultureInfo.GetCultureInfo("de-DE");
        var lastAttempt = Insights.History.FirstOrDefault();
        if (lastAttempt is not null)
        {
            LastWpm = lastAttempt.Wpm.ToString("0.0", culture);
            LastAccuracy = lastAttempt.Accuracy.ToString("0.0", culture);
        }
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
        5,
        0,
        1,
        [],
        [],
        []);
}
