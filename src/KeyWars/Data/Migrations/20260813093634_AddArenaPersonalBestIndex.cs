using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArenaPersonalBestIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_TargetTextHash_Mode_AbortedByServer",
                table: "LiveRoomSummaries",
                columns: new[] { "TargetTextHash", "Mode", "AbortedByServer" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LiveRoomSummaries_TargetTextHash_Mode_AbortedByServer",
                table: "LiveRoomSummaries");
        }
    }
}
