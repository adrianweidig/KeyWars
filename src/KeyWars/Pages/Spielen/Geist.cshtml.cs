using KeyWars.Auth;
using KeyWars.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Spielen;

public sealed class GeistModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public Guid? TextId { get; private set; }
    public string ReferenceWpm { get; private set; } = "-";
    public string ReferenceAccuracy { get; private set; } = "-";

    public async Task<IActionResult> OnGetAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var attempt = await db.TypingAttempts.SingleOrDefaultAsync(item => item.Id == attemptId && item.UserProfileId == profile.Id, cancellationToken);
        if (attempt is null)
        {
            return NotFound();
        }

        TextId = attempt.TrainingTextId;
        ReferenceWpm = attempt.Wpm.ToString("0.0");
        ReferenceAccuracy = attempt.Accuracy.ToString("0.0");
        return Page();
    }
}
