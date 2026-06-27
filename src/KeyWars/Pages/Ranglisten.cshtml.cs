using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages;

public sealed class RanglistenModel(CurrentUser currentUser, CompetitionLeaderboardService leaderboards) : PageModel
{
    public CompetitionOverview Overview { get; private set; } = EmptyOverview;

    public IReadOnlyList<(CompetitionBoardKind Kind, string Value, string Label)> Boards { get; } =
    [
        (CompetitionBoardKind.ArenaRating, "arena", "Arena"),
        (CompetitionBoardKind.Sprint, "sprint", "Sprint"),
        (CompetitionBoardKind.Text, "text", "Texte"),
        (CompetitionBoardKind.Challenge, "challenge", "Challenges"),
        (CompetitionBoardKind.Xp, "xp", "XP")
    ];

    public IReadOnlyList<(CompetitionPeriod Period, string Value, string Label)> Periods { get; } =
    [
        (CompetitionPeriod.Day, "day", "24h"),
        (CompetitionPeriod.Week, "week", "7 Tage"),
        (CompetitionPeriod.Month, "month", "30 Tage"),
        (CompetitionPeriod.AllTime, "all", "Allzeit")
    ];

    public IReadOnlyList<(TrainingMode Mode, string Value, string Label)> Modes { get; } =
    [
        (TrainingMode.Sprint15, "sprint15", DisplayNames.For(TrainingMode.Sprint15)),
        (TrainingMode.Sprint30, "sprint30", DisplayNames.For(TrainingMode.Sprint30)),
        (TrainingMode.Sprint60, "sprint60", DisplayNames.For(TrainingMode.Sprint60)),
        (TrainingMode.Sprint120, "sprint120", DisplayNames.For(TrainingMode.Sprint120)),
        (TrainingMode.Words10, "words10", DisplayNames.For(TrainingMode.Words10)),
        (TrainingMode.Words25, "words25", DisplayNames.For(TrainingMode.Words25)),
        (TrainingMode.Words50, "words50", DisplayNames.For(TrainingMode.Words50)),
        (TrainingMode.Words100, "words100", DisplayNames.For(TrainingMode.Words100))
    ];

    public async Task OnGetAsync(string? board, string? period, string? mode, Guid? textId, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Overview = await leaderboards.GetAsync(
            profile,
            new LeaderboardQuery(ParseBoard(board), ParsePeriod(period), ParseMode(mode), textId),
            cancellationToken);
    }

    public string BoardValue(CompetitionBoardKind kind) => Boards.First(item => item.Kind == kind).Value;

    public string PeriodValue(CompetitionPeriod period) => Periods.First(item => item.Period == period).Value;

    public string ModeValue(TrainingMode mode) => Modes.First(item => item.Mode == mode).Value;

    private static CompetitionBoardKind ParseBoard(string? value) => value?.ToLowerInvariant() switch
    {
        "sprint" => CompetitionBoardKind.Sprint,
        "text" => CompetitionBoardKind.Text,
        "challenge" => CompetitionBoardKind.Challenge,
        "xp" => CompetitionBoardKind.Xp,
        _ => CompetitionBoardKind.ArenaRating
    };

    private static CompetitionPeriod ParsePeriod(string? value) => value?.ToLowerInvariant() switch
    {
        "week" => CompetitionPeriod.Week,
        "month" => CompetitionPeriod.Month,
        "all" => CompetitionPeriod.AllTime,
        _ => CompetitionPeriod.Day
    };

    private static TrainingMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "sprint15" => TrainingMode.Sprint15,
        "sprint30" => TrainingMode.Sprint30,
        "sprint120" => TrainingMode.Sprint120,
        "words10" => TrainingMode.Words10,
        "words25" => TrainingMode.Words25,
        "words50" => TrainingMode.Words50,
        "words100" => TrainingMode.Words100,
        _ => TrainingMode.Sprint60
    };

    private static readonly CompetitionOverview EmptyOverview = new(
        new LeaderboardQuery(CompetitionBoardKind.ArenaRating, CompetitionPeriod.Day, TrainingMode.Sprint60, null),
        true,
        "Bronze",
        "-",
        [],
        new LeaderboardBoard(
            CompetitionBoardKind.ArenaRating,
            CompetitionPeriod.Day,
            "Arena-Rating",
            "",
            "Rating",
            [],
            null,
            null,
            "Noch keine Ranglistendaten."));
}
