using System.ComponentModel.DataAnnotations;

namespace KeyWars.Domain;

public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    [MaxLength(64)]
    public string DirectoryObjectGuid { get; set; } = "";
    [MaxLength(256)]
    public string DirectorySid { get; set; } = "";
    [MaxLength(128)]
    public string SamAccountName { get; set; } = "";
    [MaxLength(256)]
    public string UserPrincipalName { get; set; } = "";
    [MaxLength(160)]
    public string DisplayName { get; set; } = "";
    [MaxLength(120)]
    public string? GivenName { get; set; }
    [MaxLength(120)]
    public string? Surname { get; set; }
    [MaxLength(256)]
    public string? Email { get; set; }
    [MaxLength(160)]
    public string? Department { get; set; }
    [MaxLength(160)]
    public string? Title { get; set; }
    [MaxLength(32)]
    public string AccentKey { get; set; } = "cyan";
    [MaxLength(120)]
    public string? Motto { get; set; }
    public TrainingMode PreferredMode { get; set; } = TrainingMode.Sprint60;
    public int PreferredSprintSeconds { get; set; } = 60;
    public bool ShowLiveWpm { get; set; } = true;
    public bool ShowLiveRankChanges { get; set; } = true;
    public bool SoundEnabled { get; set; }
    public bool ReactionsEnabled { get; set; } = true;
    public bool ReducedMotion { get; set; }
    [MaxLength(32)]
    public string ThemePreference { get; set; } = "system";
    public bool LeaderboardVisible { get; set; } = true;
    public bool GhostSharingEnabled { get; set; }
    public bool ChallengesEnabled { get; set; } = true;
    public int DefaultChallengeExpiryDays { get; set; } = 7;
    public int ArenaRating { get; set; } = 1000;
    public int RatedMatchCount { get; set; }
    public int SeasonPoints { get; set; }
    public int ExperiencePoints { get; set; }
    public int Level { get; set; } = 1;
    public int CurrentStreakDays { get; set; }
    public DateOnly? LastActivityDate { get; set; }
    public bool Deleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class TrainingText
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? OwnerProfileId { get; set; }
    [MaxLength(160)]
    public string Title { get; set; } = "";
    [MaxLength(64)]
    public string SourceKey { get; set; } = "";
    public string Body { get; set; } = "";
    public TrainingTextVisibility Visibility { get; set; } = TrainingTextVisibility.Private;
    public bool IsStandard { get; set; }
    public bool RatingEligible { get; set; }
    public int CharacterCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TextCollection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerProfileId { get; set; }
    [MaxLength(160)]
    public string Name { get; set; } = "";
    [MaxLength(400)]
    public string? Description { get; set; }
    public TrainingTextVisibility Visibility { get; set; } = TrainingTextVisibility.Private;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TextCollectionItem
{
    public Guid TextCollectionId { get; set; }
    public Guid TrainingTextId { get; set; }
    public int SortOrder { get; set; }
}

public sealed class TypingAttempt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserProfileId { get; set; }
    public Guid? TrainingTextId { get; set; }
    public TrainingMode Mode { get; set; }
    public AttemptPhase Phase { get; set; } = AttemptPhase.Prepared;
    [MaxLength(64)]
    public string StandardTextKey { get; set; } = "";
    [MaxLength(32)]
    public string Nonce { get; set; } = "";
    [MaxLength(96)]
    public string TextHash { get; set; } = "";
    public DateTimeOffset PreparedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int DurationMilliseconds { get; set; }
    public int ClientDurationMilliseconds { get; set; }
    public int CorrectCharacters { get; set; }
    public int IncorrectCharacters { get; set; }
    public int Backspaces { get; set; }
    public int FocusLosses { get; set; }
    public int TotalCharacters { get; set; }
    public double Wpm { get; set; }
    public double RawWpm { get; set; }
    public double CharactersPerMinute { get; set; }
    public double Accuracy { get; set; }
    public double Consistency { get; set; }
    public int ConsistencySampleCount { get; set; }
    public double MeanWordMilliseconds { get; set; }
    public double WordTimingVariation { get; set; }
    public bool Completed { get; set; }
    public bool Official { get; set; }
    public bool LeaderboardEligible { get; set; }
    public bool ExperienceAwarded { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TypingAttemptError
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TypingAttemptId { get; set; }
    public Guid UserProfileId { get; set; }
    public int Position { get; set; }
    public TypingErrorKind Kind { get; set; }
    [MaxLength(32)]
    public string Expected { get; set; } = "";
    [MaxLength(32)]
    public string Actual { get; set; } = "";
    [MaxLength(32)]
    public string Pattern { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Challenge
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CreatorProfileId { get; set; }
    public Guid TrainingTextId { get; set; }
    [MaxLength(160)]
    public string Title { get; set; } = "";
    public ChallengeMode Mode { get; set; } = ChallengeMode.Classic;
    public ChallengeStatus Status { get; set; } = ChallengeStatus.Open;
    public int RoundCount { get; set; } = 1;
    public bool RatingEligible { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class ChallengeParticipant
{
    public Guid ChallengeId { get; set; }
    public Guid UserProfileId { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Invited;
    public int? Placement { get; set; }
    public double RatingDelta { get; set; }
    public DateTimeOffset InvitedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class ChallengeRound
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ChallengeId { get; set; }
    public int RoundNumber { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChallengeRoundResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ChallengeRoundId { get; set; }
    public Guid UserProfileId { get; set; }
    public Guid? TypingAttemptId { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Invited;
    public int? Placement { get; set; }
    public int DurationMilliseconds { get; set; }
    public double Wpm { get; set; }
    public double Accuracy { get; set; }
    public double Consistency { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class ChallengeAttemptBinding
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ChallengeId { get; set; }
    public Guid ChallengeRoundId { get; set; }
    public Guid UserProfileId { get; set; }
    public Guid TypingAttemptId { get; set; }
    [MaxLength(96)]
    public string TextSnapshotHash { get; set; } = "";
    public TrainingMode Mode { get; set; } = TrainingMode.Text;
    [MaxLength(32)]
    public string BindingToken { get; set; } = "";
    public bool Consumed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class LiveRoomSummary
{
    public Guid Id { get; set; }
    public int RoundNumber { get; set; } = 1;
    public int RoundVersion { get; set; } = 1;
    [MaxLength(80)]
    public string IdempotencyKey { get; set; } = "";
    public Guid CreatorProfileId { get; set; }
    [MaxLength(16)]
    public string RoomCode { get; set; } = "";
    public LiveRoomMode Mode { get; set; }
    public LiveRoomVisibility Visibility { get; set; }
    public int RoundCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool AbortedByServer { get; set; }
}

public sealed class LiveRoomParticipantSummary
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LiveRoomSummaryId { get; set; }
    public Guid UserProfileId { get; set; }
    public ParticipantStatus Status { get; set; }
    public int? Placement { get; set; }
    public int DurationMilliseconds { get; set; }
    public double Wpm { get; set; }
    public double Accuracy { get; set; }
    public double RatingDelta { get; set; }
}

public sealed class Mission
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserProfileId { get; set; }
    [MaxLength(80)]
    public string Key { get; set; } = "";
    [MaxLength(160)]
    public string Title { get; set; } = "";
    [MaxLength(360)]
    public string Description { get; set; } = "";
    public DateOnly MissionDate { get; set; }
    public int TargetValue { get; set; }
    public int CurrentValue { get; set; }
    public bool Completed { get; set; }
    public int XpReward { get; set; } = 25;
}

public sealed class RewardLedgerEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserProfileId { get; set; }
    [MaxLength(64)]
    public string Source { get; set; } = "";
    [MaxLength(80)]
    public string SourceId { get; set; } = "";
    public int Xp { get; set; }
    public DateTimeOffset AwardedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Achievement
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserProfileId { get; set; }
    [MaxLength(80)]
    public string Key { get; set; } = "";
    [MaxLength(160)]
    public string Title { get; set; } = "";
    [MaxLength(360)]
    public string Description { get; set; } = "";
    public DateTimeOffset UnlockedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WeaknessObservation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserProfileId { get; set; }
    [MaxLength(16)]
    public string Pattern { get; set; } = "";
    public int Attempts { get; set; }
    public int Errors { get; set; }
    public double AverageMilliseconds { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
