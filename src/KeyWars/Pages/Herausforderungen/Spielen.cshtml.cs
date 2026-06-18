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

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await challenges.JoinAsync(id, profile.Id, cancellationToken);
        CurrentChallenge = await db.Challenges.SingleAsync(item => item.Id == id, cancellationToken);
        Text = await db.TrainingTexts.SingleAsync(item => item.Id == CurrentChallenge.TrainingTextId, cancellationToken);
        return Page();
    }
}
