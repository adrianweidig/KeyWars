namespace KeyWars.Domain;

public enum CompetitionBoardKind
{
    ArenaRating,
    Sprint,
    Text,
    Challenge,
    Xp
}

public enum CompetitionPeriod
{
    Day,
    Week,
    Month,
    AllTime
}

public static class CompetitionEligibility
{
    public const double MinimumAccuracy = 90d;

    public static readonly TrainingMode[] StandardizedModes =
    [
        TrainingMode.Sprint15,
        TrainingMode.Sprint30,
        TrainingMode.Sprint60,
        TrainingMode.Sprint120,
        TrainingMode.Words10,
        TrainingMode.Words25,
        TrainingMode.Words50,
        TrainingMode.Words100
    ];

    public static bool IsStandardizedMode(TrainingMode mode) =>
        StandardizedModes.Contains(mode);

    public static bool CanEnterLeaderboardAtStart(TrainingMode mode, TrainingText? text) =>
        IsStandardizedMode(mode) || (mode == TrainingMode.Text && text?.RatingEligible == true);

    public static bool IsAttemptEligible(TypingAttempt attempt) =>
        attempt.LeaderboardEligible &&
        attempt.Official &&
        attempt.Completed &&
        attempt.Phase == AttemptPhase.Finished &&
        attempt.Accuracy >= MinimumAccuracy &&
        (IsStandardizedMode(attempt.Mode) || attempt.Mode == TrainingMode.Text);
}
