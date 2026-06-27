using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompetitionLeaderboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_LiveRoomParticipantSummaries_UserProfileId" """);
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_ChallengeRoundResults_UserProfileId" """);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_LeaderboardVisible_Deleted",
                table: "UserProfiles",
                columns: new[] { "LeaderboardVisible", "Deleted" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_LeaderboardEligible_Phase_Completed_Official_Mode_FinishedAt",
                table: "TypingAttempts",
                columns: new[] { "LeaderboardEligible", "Phase", "Completed", "Official", "Mode", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_TrainingTextId_LeaderboardEligible_Wpm",
                table: "TypingAttempts",
                columns: new[] { "TrainingTextId", "LeaderboardEligible", "Wpm" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_FinishedAt_AbortedByServer",
                table: "LiveRoomSummaries",
                columns: new[] { "FinishedAt", "AbortedByServer" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_LiveRoomSummaryId_Status",
                table: "LiveRoomParticipantSummaries",
                columns: new[] { "LiveRoomSummaryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status",
                table: "LiveRoomParticipantSummaries",
                columns: new[] { "UserProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_Status_FinishedAt",
                table: "Challenges",
                columns: new[] { "Status", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_UserProfileId_Status_FinishedAt",
                table: "ChallengeRoundResults",
                columns: new[] { "UserProfileId", "Status", "FinishedAt" });

            migrationBuilder.Sql("""
                UPDATE TypingAttempts
                SET LeaderboardEligible = 0
                WHERE NOT (
                    Mode IN ('Sprint15', 'Sprint30', 'Sprint60', 'Sprint120', 'Words10', 'Words25', 'Words50', 'Words100')
                    OR (
                        Mode = 'Text'
                        AND TrainingTextId IS NOT NULL
                        AND EXISTS (
                            SELECT 1
                            FROM TrainingTexts
                            WHERE TrainingTexts.Id = TypingAttempts.TrainingTextId
                              AND TrainingTexts.RatingEligible = 1
                        )
                    )
                )
                """);

            migrationBuilder.Sql("""
                UPDATE TypingAttempts
                SET LeaderboardEligible = 1
                WHERE Official = 1
                  AND (
                    Mode IN ('Sprint15', 'Sprint30', 'Sprint60', 'Sprint120', 'Words10', 'Words25', 'Words50', 'Words100')
                    OR (
                        Mode = 'Text'
                        AND TrainingTextId IS NOT NULL
                        AND EXISTS (
                            SELECT 1
                            FROM TrainingTexts
                            WHERE TrainingTexts.Id = TypingAttempts.TrainingTextId
                              AND TrainingTexts.RatingEligible = 1
                        )
                    )
                  )
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_LeaderboardVisible_Deleted",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_LeaderboardEligible_Phase_Completed_Official_Mode_FinishedAt",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_TrainingTextId_LeaderboardEligible_Wpm",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_LiveRoomSummaries_FinishedAt_AbortedByServer",
                table: "LiveRoomSummaries");

            migrationBuilder.DropIndex(
                name: "IX_LiveRoomParticipantSummaries_LiveRoomSummaryId_Status",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId_Status",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropIndex(
                name: "IX_Challenges_Status_FinishedAt",
                table: "Challenges");

            migrationBuilder.DropIndex(
                name: "IX_ChallengeRoundResults_UserProfileId_Status_FinishedAt",
                table: "ChallengeRoundResults");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomParticipantSummaries_UserProfileId",
                table: "LiveRoomParticipantSummaries",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeRoundResults_UserProfileId",
                table: "ChallengeRoundResults",
                column: "UserProfileId");
        }
    }
}
