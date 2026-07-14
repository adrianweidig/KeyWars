using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class WoerterModel : PageModel
{
    private const int DefaultWords = 25;

    public IReadOnlyList<(TrainingMode Mode, int Words)> Modes { get; } =
    [
        (TrainingMode.Words10, 10),
        (TrainingMode.Words25, 25),
        (TrainingMode.Words50, 50),
        (TrainingMode.Words100, 100)
    ];

    public TrainingMode SelectedMode { get; private set; } = TrainingMode.Words25;

    public int SelectedWords { get; private set; } = DefaultWords;

    public void OnGet(int? words)
    {
        SelectedWords = words is not null && Modes.Any(item => item.Words == words)
            ? words.Value
            : DefaultWords;
        SelectedMode = Modes.Single(item => item.Words == SelectedWords).Mode;
    }
}
