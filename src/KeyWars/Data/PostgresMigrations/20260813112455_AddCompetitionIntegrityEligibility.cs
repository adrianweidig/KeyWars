using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddCompetitionIntegrityEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropIndex(
                name: "IX_ChallengeRoundResults_Status_FinishedAt_UserProfileId",
                table: "ChallengeRoundResults");

            migrationBuilder.AddColumn<bool>(
                name: "CompetitionIntegrityEligible",
                table: "TypingAttempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CompetitionEligible",
                table: "LiveRoomParticipantSummaries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CompetitionEligible",
                table: "ChallengeRoundResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_UserProfileId_Completed_Official_Competition~",
                table: "TypingAttempts",
                columns: new[] { "UserProfileId", "Completed", "Official", "CompetitionIntegrityEligible", "Wpm" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status_Competiti~",
                table: "LiveRoomParticipantSummaries",
                columns: new[] { "UserProfileId", "Status", "CompetitionEligible" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_Status_CompetitionEligible_FinishedAt~",
                table: "ChallengeRoundResults",
                columns: new[] { "Status", "CompetitionEligible", "FinishedAt", "UserProfileId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_UserProfileId_Completed_Official_Competition~",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status_Competiti~",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropIndex(
                name: "IX_ChallengeRoundResults_Status_CompetitionEligible_FinishedAt~",
                table: "ChallengeRoundResults");

            migrationBuilder.DropColumn(
                name: "CompetitionIntegrityEligible",
                table: "TypingAttempts");

            migrationBuilder.DropColumn(
                name: "CompetitionEligible",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropColumn(
                name: "CompetitionEligible",
                table: "ChallengeRoundResults");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status",
                table: "LiveRoomParticipantSummaries",
                columns: new[] { "UserProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_Status_FinishedAt_UserProfileId",
                table: "ChallengeRoundResults",
                columns: new[] { "Status", "FinishedAt", "UserProfileId" });
        }
    }
}
