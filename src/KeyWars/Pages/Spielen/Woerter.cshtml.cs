using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class WoerterModel : PageModel
{
    public IReadOnlyList<(TrainingMode Mode, int Words)> Modes { get; } =
    [
        (TrainingMode.Words10, 10),
        (TrainingMode.Words25, 25),
        (TrainingMode.Words50, 50),
        (TrainingMode.Words100, 100)
    ];
}
