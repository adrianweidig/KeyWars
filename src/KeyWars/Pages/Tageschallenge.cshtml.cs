using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages;

public sealed class TageschallengeModel(KeyWarsDbContext db) : PageModel
{
    public TrainingText Text { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var texts = await db.TrainingTexts.Where(item => item.IsStandard).OrderBy(item => item.SourceKey).ToListAsync(cancellationToken);
        var index = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber % Math.Max(1, texts.Count);
        Text = texts.ElementAtOrDefault(index) ?? BuildFallbackText();
    }

    private static TrainingText BuildFallbackText()
    {
        var body = TypingEngine.BuildWordTest(60);
        return new TrainingText { Title = "Tageschallenge", Body = body, CharacterCount = body.Length };
    }
}
