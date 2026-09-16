using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddSeasonScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SeasonId",
                table: "RewardLedgerEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonPoints",
                table: "RewardLedgerEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TargetTextHash",
                table: "LiveRoomSummaries",
                type: "character varying(71)",
                maxLength: 71,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Seasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeasonScores",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonScores", x => new { x.SeasonId, x.UserProfileId });
                    table.ForeignKey(
                        name: "FK_SeasonScores_Seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeasonScores_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntries_SeasonId_UserProfileId",
                table: "RewardLedgerEntries",
                columns: new[] { "SeasonId", "UserProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_Key",
                table: "Seasons",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_StartsAt_EndsAt",
                table: "Seasons",
                columns: new[] { "StartsAt", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonScores_SeasonId_Points_UserProfileId",
                table: "SeasonScores",
                columns: new[] { "SeasonId", "Points", "UserProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonScores_UserProfileId",
                table: "SeasonScores",
                column: "UserProfileId");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeasonScores");

            migrationBuilder.DropTable(
                name: "Seasons");

            migrationBuilder.DropIndex(
                name: "IX_RewardLedgerEntries_SeasonId_UserProfileId",
                table: "RewardLedgerEntries");

            migrationBuilder.DropColumn(
                name: "SeasonId",
                table: "RewardLedgerEntries");

            migrationBuilder.DropColumn(
                name: "SeasonPoints",
                table: "RewardLedgerEntries");

            migrationBuilder.DropColumn(
                name: "TargetTextHash",
                table: "LiveRoomSummaries");
        }
    }
}
