using KeyWars.Domain;

namespace KeyWars.Services;

public sealed record StartAttemptRequest(TrainingMode Mode, Guid? TrainingTextId, int? SprintSeconds, int? WordCount);
public sealed record BeginAttemptRequest(Guid AttemptId, string Nonce);
public sealed record AttemptBeginResponse(Guid AttemptId, DateTimeOffset StartedAt);
public sealed record FinishAttemptRequest(Guid AttemptId, string Input, int Backspaces, int FocusLosses, int ClientDurationMilliseconds)
{
    public string Nonce { get; init; } = "";
    public IReadOnlyList<int>? WordDurationsMilliseconds { get; init; } = [];
}

public sealed record AttemptCompletion(TypingAttempt Attempt, MotivationOutcome Motivation)
{
    public Guid Id => Attempt.Id;
    public AttemptPhase Phase => Attempt.Phase;
    public DateTimeOffset PreparedAt => Attempt.PreparedAt;
    public DateTimeOffset StartedAt => Attempt.StartedAt;
    public DateTimeOffset? FinishedAt => Attempt.FinishedAt;
    public int DurationMilliseconds => Attempt.DurationMilliseconds;
    public int ClientDurationMilliseconds => Attempt.ClientDurationMilliseconds;
    public int CorrectCharacters => Attempt.CorrectCharacters;
    public int IncorrectCharacters => Attempt.IncorrectCharacters;
    public int TotalCharacters => Attempt.TotalCharacters;
    public double Wpm => Attempt.Wpm;
    public double RawWpm => Attempt.RawWpm;
    public double Accuracy => Attempt.Accuracy;
    public double Consistency => Attempt.Consistency;
    public int ConsistencySampleCount => Attempt.ConsistencySampleCount;
    public double WordTimingVariation => Attempt.WordTimingVariation;
    public bool Completed => Attempt.Completed;
    public bool ExperienceAwarded => Attempt.ExperienceAwarded;
    public string TextHash => Attempt.TextHash;

    public static implicit operator TypingAttempt(AttemptCompletion completion) => completion.Attempt;
}

public sealed record AttemptSession(
    Guid Id,
    Guid UserProfileId,
    string Text,
    TrainingMode Mode,
    DateTimeOffset PreparedAt,
    DateTimeOffset? StartedAt,
    string Nonce,
    AttemptPhase Phase);
