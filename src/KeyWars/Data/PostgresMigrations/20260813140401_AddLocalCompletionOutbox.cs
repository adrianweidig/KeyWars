using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddLocalCompletionOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveRoomCompletionOutboxEntries",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    EnqueuedAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    NextAttemptAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    LastError = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveRoomCompletionOutboxEntries", x => x.RoomId);
                });

            migrationBuilder.CreateTable(
                name: "LiveRoomCompletionOutboxProfiles",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveRoomCompletionOutboxProfiles", x => new { x.RoomId, x.UserProfileId });
                    table.ForeignKey(
                        name: "FK_LiveRoomCompletionOutboxProfiles_LiveRoomCompletionOutboxEn~",
                        column: x => x.RoomId,
                        principalTable: "LiveRoomCompletionOutboxEntries",
                        principalColumn: "RoomId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomCompletionOutboxEntries_IdempotencyKey",
                table: "LiveRoomCompletionOutboxEntries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomCompletionOutboxEntries_State_NextAttemptAtUnixMill~",
                table: "LiveRoomCompletionOutboxEntries",
                columns: new[] { "State", "NextAttemptAtUnixMilliseconds", "EnqueuedAtUnixMilliseconds", "RoomId" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveRoomCompletionOutboxProfiles_UserProfileId_RoomId",
                table: "LiveRoomCompletionOutboxProfiles",
                columns: new[] { "UserProfileId", "RoomId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveRoomCompletionOutboxProfiles");

            migrationBuilder.DropTable(
                name: "LiveRoomCompletionOutboxEntries");
        }
    }
}
