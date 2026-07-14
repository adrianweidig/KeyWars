using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Spielen;

public sealed class SprintModel : PageModel
{
    private const int DefaultSeconds = 60;

    public IReadOnlyList<(TrainingMode Mode, int Seconds)> Modes { get; } =
    [
        (TrainingMode.Sprint15, 15),
        (TrainingMode.Sprint30, 30),
        (TrainingMode.Sprint60, 60),
        (TrainingMode.Sprint120, 120)
    ];

    public TrainingMode SelectedMode { get; private set; } = TrainingMode.Sprint60;

    public int SelectedSeconds { get; private set; } = DefaultSeconds;

    public void OnGet(int? seconds)
    {
        SelectedSeconds = seconds is not null && Modes.Any(item => item.Seconds == seconds)
            ? seconds.Value
            : DefaultSeconds;
        SelectedMode = Modes.Single(item => item.Seconds == SelectedSeconds).Mode;
    }
}
