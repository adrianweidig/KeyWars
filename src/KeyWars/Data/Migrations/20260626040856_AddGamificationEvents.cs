using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGamificationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GamificationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 360, nullable: false),
                    XpDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    LevelBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    LevelAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    Rarity = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamificationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GamificationEvents_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_UserProfileId_CreatedAt",
                table: "GamificationEvents",
                columns: new[] { "UserProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GamificationEvents_UserProfileId_Source_SourceId_EventKey",
                table: "GamificationEvents",
                columns: new[] { "UserProfileId", "Source", "SourceId", "EventKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GamificationEvents");
        }
    }
}
