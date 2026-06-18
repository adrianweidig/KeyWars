using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Herausforderungen;

public sealed class DetailsModel(CurrentUser currentUser, KeyWarsDbContext db, ChallengeService challenges) : PageModel
{
    public Challenge CurrentChallenge { get; private set; } = new();
    public TrainingText Text { get; private set; } = new();
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostJoinAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await challenges.JoinAsync(id, profile.Id, cancellationToken);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeclineAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await challenges.DeclineAsync(id, profile.Id, cancellationToken);
        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var allowed = await db.ChallengeParticipants.AnyAsync(item => item.ChallengeId == id && item.UserProfileId == profile.Id, cancellationToken);
        if (!allowed)
        {
            throw new InvalidOperationException("Du bist nicht Teilnehmer dieser Herausforderung.");
        }

        CurrentChallenge = await db.Challenges.SingleAsync(item => item.Id == id, cancellationToken);
        Text = await db.TrainingTexts.SingleAsync(item => item.Id == CurrentChallenge.TrainingTextId, cancellationToken);
        Rows = await (
            from participant in db.ChallengeParticipants
            join user in db.UserProfiles on participant.UserProfileId equals user.Id
            where participant.ChallengeId == id
            orderby participant.Placement ?? int.MaxValue, user.DisplayName
            select new Row(user.DisplayName, participant.Status, participant.Placement, participant.RatingDelta)
        ).ToListAsync(cancellationToken);
    }

    public sealed record Row(string DisplayName, ParticipantStatus Status, int? Placement, double RatingDelta);
}
