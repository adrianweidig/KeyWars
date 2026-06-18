using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectoryObjectGuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DirectorySid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SamAccountName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UserPrincipalName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    GivenName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Surname = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Department = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AccentKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Motto = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PreferredMode = table.Column<string>(type: "TEXT", nullable: false),
                    PreferredSprintSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowLiveWpm = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowLiveRankChanges = table.Column<bool>(type: "INTEGER", nullable: false),
                    SoundEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReactionsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReducedMotion = table.Column<bool>(type: "INTEGER", nullable: false),
                    ThemePreference = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LeaderboardVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    GhostSharingEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChallengesEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultChallengeExpiryDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ArenaRating = table.Column<int>(type: "INTEGER", nullable: false),
                    RatedMatchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    ExperiencePoints = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStreakDays = table.Column<int>(type: "INTEGER", nullable: false),
                    LastActivityDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Achievements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 360, nullable: false),
                    UnlockedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                name: "LiveRoomSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Visibility = table.Column<string>(type: "TEXT", nullable: false),
                    RoundCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AbortedByServer = table.Column<bool>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 360, nullable: false),
                    MissionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TargetValue = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentValue = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    XpReward = table.Column<int>(type: "INTEGER", nullable: false)
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
                name: "TextCollections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Visibility = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Visibility = table.Column<string>(type: "TEXT", nullable: false),
                    IsStandard = table.Column<bool>(type: "INTEGER", nullable: false),
                    RatingEligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    CharacterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Errors = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageMilliseconds = table.Column<double>(type: "REAL", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LiveRoomSummaryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Placement = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Wpm = table.Column<double>(type: "REAL", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    RatingDelta = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveRoomParticipantSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveRoomParticipantSummaries_LiveRoomSummaries_LiveRoomSummaryId",
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrainingTextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RoundCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RatingEligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
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
                    TextCollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrainingTextId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrainingTextId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    StandardTextKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Nonce = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrectCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    IncorrectCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    Backspaces = table.Column<int>(type: "INTEGER", nullable: false),
                    FocusLosses = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    Wpm = table.Column<double>(type: "REAL", nullable: false),
                    RawWpm = table.Column<double>(type: "REAL", nullable: false),
                    CharactersPerMinute = table.Column<double>(type: "REAL", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    Consistency = table.Column<double>(type: "REAL", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Official = table.Column<bool>(type: "INTEGER", nullable: false),
                    LeaderboardEligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExperienceAwarded = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Placement = table.Column<int>(type: "INTEGER", nullable: true),
                    RatingDelta = table.Column<double>(type: "REAL", nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                name: "ChallengeRoundResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeRoundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TypingAttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Placement = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Wpm = table.Column<double>(type: "REAL", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    Consistency = table.Column<double>(type: "REAL", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
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
                name: "IX_ChallengeParticipants_UserProfileId",
                table: "ChallengeParticipants",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_ChallengeRoundId_UserProfileId",
                table: "ChallengeRoundResults",
                columns: new[] { "ChallengeRoundId", "UserProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_TypingAttemptId",
                table: "ChallengeRoundResults",
                column: "TypingAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_UserProfileId",
                table: "ChallengeRoundResults",
                column: "UserProfileId");

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
                name: "IX_Challenges_Status",
                table: "Challenges",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_TrainingTextId",
                table: "Challenges",
                column: "TrainingTextId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_LiveRoomSummaryId",
                table: "LiveRoomParticipantSummaries",
                column: "LiveRoomSummaryId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId",
                table: "LiveRoomParticipantSummaries",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_CreatorProfileId",
                table: "LiveRoomSummaries",
                column: "CreatorProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_RoomCode",
                table: "LiveRoomSummaries",
                column: "RoomCode");

            migrationBuilder.CreateIndex(
                name: "IX_Missions_UserProfileId_MissionDate",
                table: "Missions",
                columns: new[] { "UserProfileId", "MissionDate" });

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
                name: "IX_TypingAttempts_TrainingTextId",
                table: "TypingAttempts",
                column: "TrainingTextId");

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_UserProfileId_Mode_CreatedAt",
                table: "TypingAttempts",
                columns: new[] { "UserProfileId", "Mode", "CreatedAt" });

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
                name: "ChallengeParticipants");

            migrationBuilder.DropTable(
                name: "ChallengeRoundResults");

            migrationBuilder.DropTable(
                name: "LiveRoomParticipantSummaries");

            migrationBuilder.DropTable(
                name: "Missions");

            migrationBuilder.DropTable(
                name: "TextCollectionItems");

            migrationBuilder.DropTable(
                name: "WeaknessObservations");

            migrationBuilder.DropTable(
                name: "ChallengeRounds");

            migrationBuilder.DropTable(
                name: "TypingAttempts");

            migrationBuilder.DropTable(
                name: "LiveRoomSummaries");

            migrationBuilder.DropTable(
                name: "TextCollections");

            migrationBuilder.DropTable(
                name: "Challenges");

            migrationBuilder.DropTable(
                name: "TrainingTexts");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
