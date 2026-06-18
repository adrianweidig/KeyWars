namespace KeyWars.Domain;

public enum TrainingMode
{
    Sprint15,
    Sprint30,
    Sprint60,
    Sprint120,
    Words10,
    Words25,
    Words50,
    Words100,
    Text,
    Precision,
    Umlauts,
    NumbersAndSymbols,
    WeaknessFocus,
    Warmup,
    Endless,
    Ghost,
    RivalGhost
}

public enum AttemptPhase
{
    Prepared,
    Started,
    Finished,
    Expired,
    Aborted
}

public enum TypingErrorKind
{
    Insertion,
    Deletion,
    Substitution
}

public enum TrainingTextVisibility
{
    Private,
    Organization
}

public enum ChallengeMode
{
    Classic,
    TimeDuel,
    Precision,
    BestOf
}

public enum ChallengeStatus
{
    Open,
    Running,
    Finished,
    Expired,
    Cancelled
}

public enum ParticipantStatus
{
    Invited,
    Joined,
    Ready,
    Running,
    Finished,
    Declined,
    LeftBeforeStart,
    Dnf,
    Disconnected,
    Cancelled,
    AbortedByServer
}

public enum LiveRoomPhase
{
    Lobby,
    Countdown,
    Running,
    RoundResults,
    SeriesResults,
    Closed,
    Aborted
}

public enum LiveRoomVisibility
{
    InvitationOnly,
    Code,
    InternalOpen
}

public enum LiveRoomMode
{
    Classic,
    TimeDuel,
    Precision,
    BestOf
}
