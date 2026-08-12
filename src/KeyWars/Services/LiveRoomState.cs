using KeyWars.Domain;

namespace KeyWars.Services;

internal sealed class LiveRoomState(
    Guid id,
    Guid creatorProfileId,
    string code,
    string title,
    string text,
    LiveRoomMode mode,
    LiveRoomVisibility visibility,
    int roundCount,
    int maxParticipants,
    DateTimeOffset createdAt)
{
    public Guid Id { get; } = id;
    public Guid CreatorProfileId { get; set; } = creatorProfileId;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Text { get; } = text;
    public IReadOnlyList<string> TargetElements { get; } = TypingEngine.SplitGraphemes(text);
    public int TargetCharacterCount => TargetElements.Count;
    public LiveRoomMode Mode { get; } = mode;
    public LiveRoomVisibility Visibility { get; } = visibility;
    public int RoundCount { get; } = roundCount;
    public int MaxParticipants { get; } = maxParticipants;
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public object Gate { get; } = new();
    public Dictionary<Guid, LiveParticipantState> Participants { get; } = [];
    public HashSet<Guid> ExcludedProfileIds { get; } = [];
    public HashSet<Guid> InvitedProfileIds { get; } = [];
    public Dictionary<int, int> TeamRoundWins { get; } = [];
    public LiveRoomPhase Phase { get; set; } = LiveRoomPhase.Lobby;
    public int CurrentRound { get; set; } = 1;
    public int RoundVersion { get; set; } = 1;
    public long StateVersion { get; set; } = 1;
    public bool LobbyLocked { get; set; }
    public DateTimeOffset PhaseChangedAt { get; set; } = createdAt;
    public DateTimeOffset? CountdownStartsAt { get; set; }
    public DateTimeOffset? RaceStartsAt { get; set; }
    public DateTimeOffset? RoundEndsAt { get; set; }
    public string? CloseReason { get; set; }
    public bool Started { get; set; }
    public bool Finished { get; set; }
    public CompletionReceipt? CompletionReceipt { get; set; }
    public CompletionState? PersistenceState { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

internal sealed class LiveParticipantState(
    Guid profileId,
    string displayName,
    ParticipantStatus status,
    DateTimeOffset joinedAt,
    int? teamNumber)
{
    public Guid ProfileId { get; } = profileId;
    public DateTimeOffset JoinedAt { get; } = joinedAt;
    public string DisplayName { get; set; } = displayName;
    public ParticipantStatus Status { get; set; } = status;
    public int? TeamNumber { get; } = teamNumber;
    public bool Ready { get; set; }
    public int Sequence { get; set; }
    public int CorrectCharacters { get; set; }
    public string TypedTextPreview { get; set; } = "";
    public double Wpm { get; set; }
    public int? Placement { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public int DurationMilliseconds { get; set; }
    public double Accuracy { get; set; }
    public int SeriesPoints { get; set; }
    public int RoundWins { get; set; }
    public int FinishedRounds { get; set; }
    public int CompletedRounds { get; set; }
    public int TotalDurationMilliseconds { get; set; }
    public double TotalWpm { get; set; }
    public double TotalAccuracy { get; set; }
    public double AverageWpm => CompletedRounds == 0 ? 0 : Math.Round(TotalWpm / CompletedRounds, 2);
    public double AverageAccuracy => CompletedRounds == 0 ? 0 : Math.Round(TotalAccuracy / CompletedRounds, 2);
}
