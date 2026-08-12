using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddV05ScaleIndexesAndProductFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GamificationEvents_UserProfileId_CreatedAt",
                table: "GamificationEvents");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompletedAt",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "TrainingTexts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "TextCollections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RematchOfChallengeId",
                table: "Challenges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContentModerationAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorDisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetOwnerProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetTitle = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentModerationAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_Department_LeaderboardVisible_Deleted",
                table: "UserProfiles",
                columns: new[] { "Department", "LeaderboardVisible", "Deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_UserProfileId_Phase_Completed_CreatedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "UserProfileId", "Phase", "Completed", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntries_UserProfileId_AwardedAt",
                table: "RewardLedgerEntries",
                columns: new[] { "UserProfileId", "AwardedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_UserProfileId_CreatedAt_Id",
                table: "GamificationEvents",
                columns: new[] { "UserProfileId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_RematchOfChallengeId",
                table: "Challenges",
                column: "RematchOfChallengeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_Status_ExpiresAt",
                table: "Challenges",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_Status_FinishedAt_UserProfileId",
                table: "ChallengeRoundResults",
                columns: new[] { "Status", "FinishedAt", "UserProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_Achievements_UserProfileId_UnlockedAt",
                table: "Achievements",
                columns: new[] { "UserProfileId", "UnlockedAt" });

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

            migrationBuilder.AddForeignKey(
                name: "FK_Challenges_Challenges_RematchOfChallengeId",
                table: "Challenges",
                column: "RematchOfChallengeId",
                principalTable: "Challenges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Challenges_Challenges_RematchOfChallengeId",
                table: "Challenges");

            migrationBuilder.DropTable(
                name: "ContentModerationAuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_Department_LeaderboardVisible_Deleted",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_UserProfileId_Phase_Completed_CreatedAt_Id",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_RewardLedgerEntries_UserProfileId_AwardedAt",
                table: "RewardLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_GamificationEvents_UserProfileId_CreatedAt_Id",
                table: "GamificationEvents");

            migrationBuilder.DropIndex(
                name: "IX_Challenges_RematchOfChallengeId",
                table: "Challenges");

            migrationBuilder.DropIndex(
                name: "IX_Challenges_Status_ExpiresAt",
                table: "Challenges");

            migrationBuilder.DropIndex(
                name: "IX_ChallengeRoundResults_Status_FinishedAt_UserProfileId",
                table: "ChallengeRoundResults");

            migrationBuilder.DropIndex(
                name: "IX_Achievements_UserProfileId_UnlockedAt",
                table: "Achievements");

            migrationBuilder.DropColumn(
                name: "OnboardingCompletedAt",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "IsQuarantined",
                table: "TrainingTexts");

            migrationBuilder.DropColumn(
                name: "IsQuarantined",
                table: "TextCollections");

            migrationBuilder.DropColumn(
                name: "RematchOfChallengeId",
                table: "Challenges");

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_UserProfileId_CreatedAt",
                table: "GamificationEvents",
                columns: new[] { "UserProfileId", "CreatedAt" });
        }
    }
}
