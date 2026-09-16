using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class GeistModel(CurrentUser currentUser, RivalGhostService rivalGhosts) : PageModel
{
    public Guid SourceAttemptId { get; private set; }
    public Guid TextId { get; private set; }
    public double ReferenceWpm { get; private set; }
    public double ReferenceAccuracy { get; private set; }
    public string ReferenceName { get; private set; } = "Eigener Geist";
    public TrainingMode Mode { get; private set; } = TrainingMode.Ghost;
    public IReadOnlyList<RivalGhostOption> Rivals { get; private set; } = [];
    public RivalGhostOption? SelectedRival { get; private set; }
    public bool FallbackApplied { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid attemptId,
        Guid? rivalProfileId,
        CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var selection = await rivalGhosts.GetAsync(
            profile.Id,
            attemptId,
            rivalProfileId,
            cancellationToken);
        if (selection is null)
        {
            return NotFound();
        }

        SourceAttemptId = selection.SourceAttemptId;
        TextId = selection.TrainingTextId;
        Rivals = selection.Rivals;
        SelectedRival = selection.SelectedRival;
        FallbackApplied = selection.FallbackApplied;
        ReferenceWpm = selection.SelectedRival?.Wpm ?? selection.OwnWpm;
        ReferenceAccuracy = selection.SelectedRival?.Accuracy ?? selection.OwnAccuracy;
        ReferenceName = selection.SelectedRival?.DisplayName ?? "Eigener Geist";
        Mode = selection.SelectedRival is null ? TrainingMode.Ghost : TrainingMode.RivalGhost;
        return Page();
    }
}
