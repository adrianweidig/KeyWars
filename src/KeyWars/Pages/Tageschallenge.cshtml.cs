using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages;

public sealed class TageschallengeModel(KeyWarsDbContext db, TimeProvider timeProvider) : PageModel
{
    public TrainingText? Text { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var texts = await db.TrainingTexts
            .AsNoTracking()
            .Where(item => item.IsStandard && item.RatingEligible && !item.IsQuarantined)
            .OrderBy(item => item.SourceKey)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (texts.Count == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        Text = texts[today.DayNumber % texts.Count];
    }
}
