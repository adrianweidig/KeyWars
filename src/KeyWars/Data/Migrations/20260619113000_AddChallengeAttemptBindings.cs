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
    [Migration("20260619113000_AddChallengeAttemptBindings")]
    public partial class AddChallengeAttemptBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChallengeAttemptBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeRoundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TypingAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TextSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    BindingToken = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeAttemptBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_ChallengeRounds_ChallengeRoundId",
                        column: x => x.ChallengeRoundId,
                        principalTable: "ChallengeRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_TypingAttempts_TypingAttemptId",
                        column: x => x.TypingAttemptId,
                        principalTable: "TypingAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeAttemptBindings_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_ChallengeId",
                table: "ChallengeAttemptBindings",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_ChallengeRoundId_UserProfileId",
                table: "ChallengeAttemptBindings",
                columns: new[] { "ChallengeRoundId", "UserProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_TypingAttemptId",
                table: "ChallengeAttemptBindings",
                column: "TypingAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeAttemptBindings_UserProfileId",
                table: "ChallengeAttemptBindings",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChallengeAttemptBindings");
        }
    }
}
