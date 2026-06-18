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
    ChallengeService challenges,
    TimeProvider timeProvider) : PageModel
{
    public UserProfile Profile { get; private set; } = new();
    public IReadOnlyList<Mission> Missions { get; private set; } = [];
    public IReadOnlyList<Challenge> Challenges { get; private set; } = [];
    public CoachRecommendation Recommendation { get; private set; } = new("Starte mit einer ruhigen Runde.", TrainingMode.Sprint60, 1);
    public string LastWpm { get; private set; } = "-";
    public string LastAccuracy { get; private set; } = "-";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        await motivation.EnsureDailyMissionsAsync(Profile.Id, today, cancellationToken);
        Missions = await db.Missions.Where(item => item.UserProfileId == Profile.Id && item.MissionDate == today).ToListAsync(cancellationToken);
        Challenges = await challenges.ListForProfileAsync(Profile.Id, cancellationToken);
        Recommendation = await motivation.RecommendAsync(Profile.Id, cancellationToken);

        var lastAttempt = (await db.TypingAttempts
            .Where(item => item.UserProfileId == Profile.Id && item.Completed)
            .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        if (lastAttempt is not null)
        {
            LastWpm = lastAttempt.Wpm.ToString("0.0");
            LastAccuracy = lastAttempt.Accuracy.ToString("0.0");
        }
    }
}
