using System;
using KeyWars.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(KeyWarsDbContext))]
    [Migration("20260619123000_AddRewardLedger")]
    public partial class AddRewardLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "Missions",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE Missions SET Key = 'daily-three-rounds' WHERE Key = '' AND Title = 'Drei kurze Runden';");
            migrationBuilder.Sql("UPDATE Missions SET Key = 'daily-accuracy' WHERE Key = '' AND Title = 'Genauigkeit halten';");
            migrationBuilder.Sql("UPDATE Missions SET Key = 'daily-tempo' WHERE Key = '' AND Title = 'Tempo festigen';");
            migrationBuilder.Sql("UPDATE Missions SET Key = 'legacy-' || replace(Id, '-', '') WHERE Key = '';");

            migrationBuilder.CreateTable(
                name: "RewardLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Xp = table.Column<int>(type: "INTEGER", nullable: false),
                    AwardedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_Missions_UserProfileId_MissionDate_Key",
                table: "Missions",
                columns: new[] { "UserProfileId", "MissionDate", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedgerEntries_UserProfileId_Source_SourceId",
                table: "RewardLedgerEntries",
                columns: new[] { "UserProfileId", "Source", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RewardLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_Missions_UserProfileId_MissionDate_Key",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "Missions");
        }
    }
}
