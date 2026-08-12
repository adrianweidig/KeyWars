using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeV05Model : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_Completed_Phase_PreparedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "Completed", "Phase", "PreparedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_Phase_FinishedAt_PreparedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "Phase", "FinishedAt", "PreparedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttempts_Phase_FinishedAt_StartedAt_Id",
                table: "TypingAttempts",
                columns: new[] { "Phase", "FinishedAt", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_SeenAt_CreatedAt_Id",
                table: "GamificationEvents",
                columns: new[] { "SeenAt", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_Completed_Phase_PreparedAt_Id",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_Phase_FinishedAt_PreparedAt_Id",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TypingAttempts_Phase_FinishedAt_StartedAt_Id",
                table: "TypingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_GamificationEvents_SeenAt_CreatedAt_Id",
                table: "GamificationEvents");
        }
    }
}
