using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveRoomCompletionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RoundNumber",
                table: "LiveRoomSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RoundVersion",
                table: "LiveRoomSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "LiveRoomSummaries",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE LiveRoomSummaries SET IdempotencyKey = replace(Id, '-', '') || ':1:1' WHERE IdempotencyKey = '';");

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomSummaries_IdempotencyKey",
                table: "LiveRoomSummaries",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LiveRoomSummaries_IdempotencyKey",
                table: "LiveRoomSummaries");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "LiveRoomSummaries");

            migrationBuilder.DropColumn(
                name: "RoundNumber",
                table: "LiveRoomSummaries");

            migrationBuilder.DropColumn(
                name: "RoundVersion",
                table: "LiveRoomSummaries");
        }
    }
}
