using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class SprintModel : PageModel
{
    public IReadOnlyList<(TrainingMode Mode, int Seconds)> Modes { get; } =
    [
        (TrainingMode.Sprint15, 15),
        (TrainingMode.Sprint30, 30),
        (TrainingMode.Sprint60, 60),
        (TrainingMode.Sprint120, 120)
    ];
}
