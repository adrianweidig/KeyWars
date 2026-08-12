using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Herausforderungen;

public sealed class SpielenModel(CurrentUser currentUser, KeyWarsDbContext db, ChallengeService challenges) : PageModel
{
    public Challenge CurrentChallenge { get; private set; } = new();
    public TrainingText Text { get; private set; } = new();
    public int CurrentRound { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            await challenges.RequirePlayableAsync(id, profile.Id, cancellationToken);
        }
        catch (ChallengeLifecycleException exception)
        {
            TempData["ChallengeError"] = exception.Message;
            return RedirectToPage("/Herausforderungen/Details", new { id });
        }

        CurrentChallenge = await db.Challenges.SingleAsync(item => item.Id == id, cancellationToken);
        Text = await db.TrainingTexts.SingleAsync(item => item.Id == CurrentChallenge.TrainingTextId, cancellationToken);
        var roundIds = db.ChallengeRounds
            .Where(round => round.ChallengeId == id)
            .Select(round => round.Id);
        var completedRounds = await db.ChallengeRoundResults.CountAsync(
            result => result.UserProfileId == profile.Id && roundIds.Contains(result.ChallengeRoundId),
            cancellationToken);
        CurrentRound = Math.Min(CurrentChallenge.RoundCount, completedRounds + 1);
        return Page();
    }
}
