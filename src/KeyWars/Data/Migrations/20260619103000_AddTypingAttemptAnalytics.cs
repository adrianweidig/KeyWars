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
    [Migration("20260619103000_AddTypingAttemptAnalytics")]
    public partial class AddTypingAttemptAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsistencySampleCount",
                table: "TypingAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "MeanWordMilliseconds",
                table: "TypingAttempts",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "WordTimingVariation",
                table: "TypingAttempts",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "TypingAttemptErrors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TypingAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Expected = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Actual = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypingAttemptErrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TypingAttemptErrors_TypingAttempts_TypingAttemptId",
                        column: x => x.TypingAttemptId,
                        principalTable: "TypingAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TypingAttemptErrors_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttemptErrors_TypingAttemptId",
                table: "TypingAttemptErrors",
                column: "TypingAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_TypingAttemptErrors_UserProfileId_Pattern",
                table: "TypingAttemptErrors",
                columns: new[] { "UserProfileId", "Pattern" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TypingAttemptErrors");

            migrationBuilder.DropColumn(
                name: "ConsistencySampleCount",
                table: "TypingAttempts");

            migrationBuilder.DropColumn(
                name: "MeanWordMilliseconds",
                table: "TypingAttempts");

            migrationBuilder.DropColumn(
                name: "WordTimingVariation",
                table: "TypingAttempts");
        }
    }
}
