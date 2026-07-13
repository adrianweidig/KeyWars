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
    public ParticipantStatus CurrentParticipantStatus { get; private set; }
    public bool CanJoin => IsActive && CurrentParticipantStatus == ParticipantStatus.Invited;
    public bool CanDecline => IsActive && CurrentParticipantStatus is ParticipantStatus.Invited or ParticipantStatus.Joined;
    public bool CanPlay => IsActive && CurrentParticipantStatus is ParticipantStatus.Joined or ParticipantStatus.Ready or ParticipantStatus.Running;

    private bool IsActive => CurrentChallenge.Status is ChallengeStatus.Open or ChallengeStatus.Running;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (TempData.TryGetValue("ChallengeError", out var message) && message is string errorMessage)
        {
            ModelState.AddModelError(string.Empty, errorMessage);
        }

        await LoadAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostJoinAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            await challenges.JoinAsync(id, profile.Id, cancellationToken);
            return RedirectToPage(new { id });
        }
        catch (ChallengeLifecycleException exception)
        {
            return await ChallengeErrorAsync(id, exception, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostDeclineAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            await challenges.DeclineAsync(id, profile.Id, cancellationToken);
            return RedirectToPage(new { id });
        }
        catch (ChallengeLifecycleException exception)
        {
            return await ChallengeErrorAsync(id, exception, cancellationToken);
        }
    }

    private async Task<IActionResult> ChallengeErrorAsync(Guid id, ChallengeLifecycleException exception, CancellationToken cancellationToken)
    {
        Response.StatusCode = exception.StatusCode;
        ModelState.AddModelError(string.Empty, exception.Message);
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var currentParticipant = await db.ChallengeParticipants.SingleOrDefaultAsync(
            item => item.ChallengeId == id && item.UserProfileId == profile.Id,
            cancellationToken);
        if (currentParticipant is null)
        {
            throw new InvalidOperationException("Du bist nicht Teilnehmer dieser Herausforderung.");
        }

        CurrentParticipantStatus = currentParticipant.Status;
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
