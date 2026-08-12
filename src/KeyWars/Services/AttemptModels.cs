using KeyWars.Domain;

namespace KeyWars.Services;

public sealed record StartAttemptRequest(TrainingMode Mode, Guid? TrainingTextId, int? SprintSeconds, int? WordCount);
public sealed record BeginAttemptRequest(Guid AttemptId, string Nonce);
public sealed record AttemptBeginResponse(
    Guid AttemptId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset ServerNow)
{
    public AttemptBeginResponse(Guid attemptId, DateTimeOffset startedAt)
        : this(attemptId, startedAt, null, startedAt)
    {
    }

    public void Deconstruct(out Guid attemptId, out DateTimeOffset startedAt)
    {
        attemptId = AttemptId;
        startedAt = StartedAt;
    }
}

public static class AttemptErrorCodes
{
    public const string InvalidRequest = "attempt_invalid_request";
    public const string NotFound = "attempt_not_found";
    public const string InvalidNonce = "attempt_invalid_nonce";
    public const string NotStarted = "attempt_not_started";
    public const string StillRunning = "attempt_still_running";
    public const string Expired = "attempt_expired";
    public const string ChallengeBound = "attempt_challenge_bound";
}

public sealed class AttemptLifecycleException(
    string code,
    int statusCode,
    string message,
    int? retryAfterMs = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public int? RetryAfterMs { get; } = retryAfterMs;
}
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
