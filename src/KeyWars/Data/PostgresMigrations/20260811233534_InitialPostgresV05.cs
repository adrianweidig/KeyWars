using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class InitialPostgresV05 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentModerationAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TargetType = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetOwnerProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetTitle = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentModerationAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectoryObjectGuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DirectorySid = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SamAccountName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserPrincipalName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    GivenName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Surname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Department = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    AccentKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Motto = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PreferredMode = table.Column<string>(type: "text", nullable: false),
                    PreferredSprintSeconds = table.Column<int>(type: "integer", nullable: false),
                    ShowLiveWpm = table.Column<bool>(type: "boolean", nullable: false),
                    ShowLiveRankChanges = table.Column<bool>(type: "boolean", nullable: false),
                    SoundEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SoundVolumePercent = table.Column<int>(type: "integer", nullable: false),
                    ReactionsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ReducedMotion = table.Column<bool>(type: "boolean", nullable: false),
                    ThemePreference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LeaderboardVisible = table.Column<bool>(type: "boolean", nullable: false),
                    GhostSharingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ChallengesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultChallengeExpiryDays = table.Column<int>(type: "integer", nullable: false),
                    ArenaRating = table.Column<int>(type: "integer", nullable: false),
                    RatedMatchCount = table.Column<int>(type: "integer", nullable: false),
                    SeasonPoints = table.Column<int>(type: "integer", nullable: false),
                    ExperiencePoints = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    CurrentStreakDays = table.Column<int>(type: "integer", nullable: false),
                    LastActivityDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OnboardingCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Achievements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(360)", maxLength: 360, nullable: false),
                    UnlockedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Achievements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Achievements_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GamificationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(360)", maxLength: 360, nullable: false),
                    XpDelta = table.Column<int>(type: "integer", nullable: false),
                    LevelBefore = table.Column<int>(type: "integer", nullable: false),
                    LevelAfter = table.Column<int>(type: "integer", nullable: false),
                    Rarity = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamificationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GamificationEvents_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveRoomSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    RoundVersion = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Visibility = table.Column<string>(type: "text", nullable: false),
                    RoundCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AbortedByServer = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveRoomSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveRoomSummaries_UserProfiles_CreatorProfileId",
                        column: x => x.CreatorProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Missions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(360)", maxLength: 360, nullable: false),
                    MissionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TargetValue = table.Column<int>(type: "integer", nullable: false),
                    CurrentValue = table.Column<int>(type: "integer", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    XpReward = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Missions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Missions_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RewardLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Xp = table.Column<int>(type: "integer", nullable: false),
                    AwardedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RewardLedgerEntries_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TextCollections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Visibility = table.Column<string>(type: "text", nullable: false),
                    IsQuarantined = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextCollections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TextCollections_UserProfiles_OwnerProfileId",
                        column: x => x.OwnerProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrainingTexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Visibility = table.Column<string>(type: "text", nullable: false),
                    IsQuarantined = table.Column<bool>(type: "boolean", nullable: false),
                    IsStandard = table.Column<bool>(type: "boolean", nullable: false),
                    RatingEligible = table.Column<bool>(type: "boolean", nullable: false),
                    CharacterCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingTexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainingTexts_UserProfiles_OwnerProfileId",
                        column: x => x.OwnerProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WeaknessObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Errors = table.Column<int>(type: "integer", nullable: false),
                    AverageMilliseconds = table.Column<double>(type: "double precision", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeaknessObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeaknessObservations_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveRoomParticipantSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LiveRoomSummaryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamNumber = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Placement = table.Column<int>(type: "integer", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Wpm = table.Column<double>(type: "double precision", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    RatingBefore = table.Column<int>(type: "integer", nullable: false),
                    RatingDelta = table.Column<double>(type: "double precision", nullable: false),
                    RatingAfter = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveRoomParticipantSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveRoomParticipantSummaries_LiveRoomSummaries_LiveRoomSumm~",
                        column: x => x.LiveRoomSummaryId,
                        principalTable: "LiveRoomSummaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LiveRoomParticipantSummaries_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RematchOfChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrainingTextId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RoundCount = table.Column<int>(type: "integer", nullable: false),
                    RatingEligible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Challenges_Challenges_RematchOfChallengeId",
                        column: x => x.RematchOfChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Challenges_TrainingTexts_TrainingTextId",
                        column: x => x.TrainingTextId,
                        principalTable: "TrainingTexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Challenges_UserProfiles_CreatorProfileId",
                        column: x => x.CreatorProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TextCollectionItems",
                columns: table => new
                {
                    TextCollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrainingTextId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextCollectionItems", x => new { x.TextCollectionId, x.TrainingTextId });
                    table.ForeignKey(
                        name: "FK_TextCollectionItems_TextCollections_TextCollectionId",
                        column: x => x.TextCollectionId,
                        principalTable: "TextCollections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TextCollectionItems_TrainingTexts_TrainingTextId",
                        column: x => x.TrainingTextId,
                        principalTable: "TrainingTexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TypingAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrainingTextId = table.Column<Guid>(type: "uuid", nullable: true),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    StandardTextKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TextHash = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    ClientDurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    CorrectCharacters = table.Column<int>(type: "integer", nullable: false),
                    IncorrectCharacters = table.Column<int>(type: "integer", nullable: false),
                    Backspaces = table.Column<int>(type: "integer", nullable: false),
                    FocusLosses = table.Column<int>(type: "integer", nullable: false),
                    TotalCharacters = table.Column<int>(type: "integer", nullable: false),
                    Wpm = table.Column<double>(type: "double precision", nullable: false),
                    RawWpm = table.Column<double>(type: "double precision", nullable: false),
                    CharactersPerMinute = table.Column<double>(type: "double precision", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    Consistency = table.Column<double>(type: "double precision", nullable: false),
                    ConsistencySampleCount = table.Column<int>(type: "integer", nullable: false),
                    MeanWordMilliseconds = table.Column<double>(type: "double precision", nullable: false),
                    WordTimingVariation = table.Column<double>(type: "double precision", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    Official = table.Column<bool>(type: "boolean", nullable: false),
                    LeaderboardEligible = table.Column<bool>(type: "boolean", nullable: false),
                    ExperienceAwarded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypingAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TypingAttempts_TrainingTexts_TrainingTextId",
                        column: x => x.TrainingTextId,
                        principalTable: "TrainingTexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TypingAttempts_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeParticipants",
                columns: table => new
                {
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Placement = table.Column<int>(type: "integer", nullable: true),
                    RatingBefore = table.Column<int>(type: "integer", nullable: false),
                    RatingDelta = table.Column<double>(type: "double precision", nullable: false),
                    RatingAfter = table.Column<int>(type: "integer", nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeParticipants", x => new { x.ChallengeId, x.UserProfileId });
                    table.ForeignKey(
                        name: "FK_ChallengeParticipants_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipants_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeRounds_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TypingAttemptErrors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TypingAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Expected = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Actual = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Pattern = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypingAttemptErrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TypingAttemptErrors_TypingAttempts_TypingAttemptId",
                        column: x => x.TypingAttemptId,
                        principalTable: "TypingAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TypingAttemptErrors_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeAttemptBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeRoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypingAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    TextSnapshotHash = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    BindingToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeAttemptBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_ChallengeRounds_ChallengeRoundId",
                        column: x => x.ChallengeRoundId,
                        principalTable: "ChallengeRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_TypingAttempts_TypingAttemptId",
                        column: x => x.TypingAttemptId,
                        principalTable: "TypingAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeRoundResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeRoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypingAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Placement = table.Column<int>(type: "integer", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Wpm = table.Column<double>(type: "double precision", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    Consistency = table.Column<double>(type: "double precision", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeRoundResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeRoundResults_ChallengeRounds_ChallengeRoundId",
                        column: x => x.ChallengeRoundId,
                        principalTable: "ChallengeRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeRoundResults_TypingAttempts_TypingAttemptId",
                        column: x => x.TypingAttemptId,
                        principalTable: "TypingAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChallengeRoundResults_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Achievements_UserProfileId_Key",
                table: "Achievements",
                columns: new[] { "UserProfileId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Achievements_UserProfileId_UnlockedAt",
                table: "Achievements",
                columns: new[] { "UserProfileId", "UnlockedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_ChallengeId",
                table: "ChallengeAttemptBindings",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_ChallengeRoundId_UserProfileId",
                table: "ChallengeAttemptBindings",
                columns: new[] { "ChallengeRoundId", "UserProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_TypingAttemptId",
                table: "ChallengeAttemptBindings",
                column: "TypingAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_UserProfileId",
                table: "ChallengeAttemptBindings",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipants_UserProfileId",
                table: "ChallengeParticipants",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_ChallengeRoundId_UserProfileId",
                table: "ChallengeRoundResults",
                columns: new[] { "ChallengeRoundId", "UserProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_Status_FinishedAt_UserProfileId",
                table: "ChallengeRoundResults",
                columns: new[] { "Status", "FinishedAt", "UserProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_TypingAttemptId",
                table: "ChallengeRoundResults",
                column: "TypingAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_UserProfileId_Status_FinishedAt",
                table: "ChallengeRoundResults",
                columns: new[] { "UserProfileId", "Status", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRounds_ChallengeId_RoundNumber",
                table: "ChallengeRounds",
                columns: new[] { "ChallengeId", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_CreatorProfileId",
                table: "Challenges",
                column: "CreatorProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_RematchOfChallengeId",
                table: "Challenges",
                column: "RematchOfChallengeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_Status",
                table: "Challenges",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_Status_ExpiresAt",
                table: "Challenges",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_Status_FinishedAt",
                table: "Challenges",
                columns: new[] { "Status", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_TrainingTextId",
                table: "Challenges",
                column: "TrainingTextId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentModerationAuditEntries_ActorProfileId_CreatedAt",
                table: "ContentModerationAuditEntries",
                columns: new[] { "ActorProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentModerationAuditEntries_TargetOwnerProfileId_CreatedAt",
                table: "ContentModerationAuditEntries",
                columns: new[] { "TargetOwnerProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentModerationAuditEntries_TargetType_TargetId_CreatedAt",
                table: "ContentModerationAuditEntries",
                columns: new[] { "TargetType", "TargetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_SeenAt_CreatedAt_Id",
                table: "GamificationEvents",
                columns: new[] { "SeenAt", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_UserProfileId_CreatedAt_Id",
                table: "GamificationEvents",
                columns: new[] { "UserProfileId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_UserProfileId_Source_SourceId_EventKey",
                table: "GamificationEvents",
                columns: new[] { "UserProfileId", "Source", "SourceId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_LiveRoomSummaryId",
                table: "LiveRoomParticipantSummaries",
                column: "LiveRoomSummaryId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_LiveRoomSummaryId_Status",
                table: "LiveRoomParticipantSummaries",
                columns: new[] { "LiveRoomSummaryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status",
                table: "LiveRoomParticipantSummaries",
                columns: new[] { "UserProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_CreatorProfileId",
                table: "LiveRoomSummaries",
                column: "CreatorProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_FinishedAt_AbortedByServer",
                table: "LiveRoomSummaries",
                columns: new[] { "FinishedAt", "AbortedByServer" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_IdempotencyKey",
                table: "LiveRoomSummaries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_RoomCode",
                table: "LiveRoomSummaries",
                column: "RoomCode");

            migrationBuilder.CreateIndex(
                name: "IX_Missions_UserProfileId_MissionDate",
                table: "Missions",
                columns: new[] { "UserProfileId", "MissionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Missions_UserProfileId_MissionDate_Key",
                table: "Missions",
                columns: new[] { "UserProfileId", "MissionDate", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntries_UserProfileId_AwardedAt",
                table: "RewardLedgerEntries",
                columns: new[] { "UserProfileId", "AwardedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntries_UserProfileId_Source_SourceId",
                table: "RewardLedgerEntries",
                columns: new[] { "UserProfileId", "Source", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TextCollectionItems_TextCollectionId_SortOrder",
                table: "TextCollectionItems",
                columns: new[] { "TextCollectionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TextCollectionItems_TrainingTextId",
                table: "TextCollectionItems",
                column: "TrainingTextId");

            migrationBuilder.CreateIndex(
                name: "IX_TextCollections_OwnerProfileId",
                table: "TextCollections",
                column: "OwnerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingTexts_OwnerProfileId_Visibility",
                table: "TrainingTexts",
                columns: new[] { "OwnerProfileId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingTexts_SourceKey",
                table: "TrainingTexts",
                column: "SourceKey");

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttemptErrors_TypingAttemptId",
                table: "TypingAttemptErrors",
                column: "TypingAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttemptErrors_UserProfileId_Pattern",
                table: "TypingAttemptErrors",
                columns: new[] { "UserProfileId", "Pattern" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_Completed_Phase_PreparedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "Completed", "Phase", "PreparedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_LeaderboardEligible_Phase_Completed_Official~",
                table: "TypingAttempts",
                columns: new[] { "LeaderboardEligible", "Phase", "Completed", "Official", "Mode", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_Phase_FinishedAt_PreparedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "Phase", "FinishedAt", "PreparedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_Phase_FinishedAt_StartedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "Phase", "FinishedAt", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_TrainingTextId",
                table: "TypingAttempts",
                column: "TrainingTextId");

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_TrainingTextId_LeaderboardEligible_Wpm",
                table: "TypingAttempts",
                columns: new[] { "TrainingTextId", "LeaderboardEligible", "Wpm" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_UserProfileId_Mode_CreatedAt",
                table: "TypingAttempts",
                columns: new[] { "UserProfileId", "Mode", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_UserProfileId_Phase_Completed_CreatedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "UserProfileId", "Phase", "Completed", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_Department_LeaderboardVisible_Deleted",
                table: "UserProfiles",
                columns: new[] { "Department", "LeaderboardVisible", "Deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_DirectoryObjectGuid",
                table: "UserProfiles",
                column: "DirectoryObjectGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_DisplayName",
                table: "UserProfiles",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_LeaderboardVisible_Deleted",
                table: "UserProfiles",
                columns: new[] { "LeaderboardVisible", "Deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_SamAccountName",
                table: "UserProfiles",
                column: "SamAccountName");

            migrationBuilder.CreateIndex(
                name: "IX_WeaknessObservations_UserProfileId_Pattern",
                table: "WeaknessObservations",
                columns: new[] { "UserProfileId", "Pattern" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Achievements");

            migrationBuilder.DropTable(
                name: "ChallengeAttemptBindings");

            migrationBuilder.DropTable(
                name: "ChallengeParticipants");

            migrationBuilder.DropTable(
                name: "ChallengeRoundResults");

            migrationBuilder.DropTable(
                name: "ContentModerationAuditEntries");

            migrationBuilder.DropTable(
                name: "GamificationEvents");

            migrationBuilder.DropTable(
                name: "LiveRoomParticipantSummaries");

            migrationBuilder.DropTable(
                name: "Missions");

            migrationBuilder.DropTable(
                name: "RewardLedgerEntries");

            migrationBuilder.DropTable(
                name: "TextCollectionItems");

            migrationBuilder.DropTable(
                name: "TypingAttemptErrors");

            migrationBuilder.DropTable(
                name: "WeaknessObservations");

            migrationBuilder.DropTable(
                name: "ChallengeRounds");

            migrationBuilder.DropTable(
                name: "LiveRoomSummaries");

            migrationBuilder.DropTable(
                name: "TextCollections");

            migrationBuilder.DropTable(
                name: "TypingAttempts");

            migrationBuilder.DropTable(
                name: "Challenges");

            migrationBuilder.DropTable(
                name: "TrainingTexts");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
