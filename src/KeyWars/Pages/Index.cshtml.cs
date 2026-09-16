using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages;

public sealed class IndexModel(
    CurrentUser currentUser,
    KeyWarsDbContext db,
    MotivationService motivation,
    ProfileInsightsService insights,
    CompetitionLeaderboardService leaderboards,
    TimeProvider timeProvider) : PageModel
{
    public UserProfile Profile { get; private set; } = new();
    public IReadOnlyList<Mission> Missions { get; private set; } = [];
    public IReadOnlyList<ProfileAttemptHistoryRow> RecentResults { get; private set; } = [];
    public LeaderboardBoard DailySprintBoard { get; private set; } = EmptyDailySprintBoard;

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
        RecentResults = await insights.ReadRecentHistoryAsync(Profile.Id, 5, cancellationToken);
        DailySprintBoard = await leaderboards.GetBoardAsync(
            Profile,
            new LeaderboardQuery(CompetitionBoardKind.Sprint, CompetitionPeriod.Day, TrainingMode.Sprint60, null),
            cancellationToken);
    }

    private static readonly LeaderboardBoard EmptyDailySprintBoard = new(
        CompetitionBoardKind.Sprint,
        CompetitionPeriod.Day,
        "Tages-Sprint 60s",
        "Bestes Ergebnis der letzten 24 Stunden.",
        "WPM",
        [],
        null,
        null,
        "Noch kein sichtbarer Sprint-Wert.");
}
