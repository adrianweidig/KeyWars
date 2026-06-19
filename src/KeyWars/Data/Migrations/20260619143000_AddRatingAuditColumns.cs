using KeyWars.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(KeyWarsDbContext))]
    [Migration("20260619143000_AddRatingAuditColumns")]
    public partial class AddRatingAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RatingAfter",
                table: "LiveRoomParticipantSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<int>(
                name: "RatingBefore",
                table: "LiveRoomParticipantSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<int>(
                name: "RatingAfter",
                table: "ChallengeParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<int>(
                name: "RatingBefore",
                table: "ChallengeParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RatingAfter",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropColumn(
                name: "RatingBefore",
                table: "LiveRoomParticipantSummaries");

            migrationBuilder.DropColumn(
                name: "RatingAfter",
                table: "ChallengeParticipants");

            migrationBuilder.DropColumn(
                name: "RatingBefore",
                table: "ChallengeParticipants");
        }
    }
}
