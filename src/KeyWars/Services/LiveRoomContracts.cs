using KeyWars.Domain;

namespace KeyWars.Services;

public sealed record LiveParticipantSnapshot(
    Guid ProfileId,
    string DisplayName,
    ParticipantStatus Status,
    bool Ready,
    int Sequence,
    int CorrectCharacters,
    string TypedTextPreview,
    double Wpm,
    int? Placement,
    int DurationMilliseconds,
    double Accuracy,
    int? TeamNumber = null,
    int SeriesPoints = 0,
    int RoundWins = 0);

public sealed record LiveTeamSnapshot(
    int TeamNumber,
    string Name,
    int Points,
    int RoundWins,
    int FinishedRounds,
    int? Placement);

public sealed record LiveRoomSnapshot(
    Guid RoomId,
    Guid CreatorProfileId,
    string Code,
    string Title,
    string TargetText,
    int TargetCharacterCount,
    int MaxParticipants,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    int CurrentRound,
    int RoundVersion,
    LiveRoomPhase Phase,
    bool Started,
    bool Finished,
    DateTimeOffset ServerNow,
    DateTimeOffset PhaseChangedAt,
    DateTimeOffset? CountdownStartsAt,
    DateTimeOffset? RaceStartsAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CloseReason,
    IReadOnlyList<LiveParticipantSnapshot> Participants,
    CompletionState? PersistenceState = null,
    IReadOnlyList<LiveTeamSnapshot>? Teams = null,
    DateTimeOffset? RoundEndsAt = null,
    long StateVersion = 1,
    bool LobbyLocked = false);

public sealed record LiveProgressResult(
    LiveProgressDelta? Delta,
    LiveRoomSnapshot? Snapshot);

public sealed record CreateLiveRoomRequest(
    Guid CreatorProfileId,
    string CreatorDisplayName,
    string Title,
    string Text,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    int MaxParticipants,
    IReadOnlyList<LiveRoomInvitation>? Invitations = null);

public sealed record LiveRoomInvitation(Guid ProfileId, string DisplayName);

public sealed record LiveRoomLobbySummary(
    Guid RoomId,
    Guid CreatorProfileId,
    string CreatorDisplayName,
    string Code,
    string Title,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    LiveRoomPhase Phase,
    int RoundCount,
    int CurrentRound,
    int ParticipantCount,
    int MaxParticipants,
    bool LobbyLocked,
    long StateVersion);

public sealed record LiveRoomLobbyPage(
    IReadOnlyList<LiveRoomLobbySummary> Items,
    int Offset,
    int Limit,
    int Total);

public sealed record LiveRoomMetricsSnapshot(
    int ActiveRooms,
    int OpenRooms,
    int RunningRooms,
    int Participants);

public sealed record CompletedRoomRecord(
    Guid Id,
    int RoundNumber,
    int RoundVersion,
    string IdempotencyKey,
    Guid CreatorProfileId,
    string RoomCode,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<CompletedParticipantRecord> Participants);

public sealed record CompletedParticipantRecord(
    Guid UserProfileId,
    ParticipantStatus Status,
    int? Placement,
    int DurationMilliseconds,
    double Wpm,
    double Accuracy,
    int? TeamNumber = null);
