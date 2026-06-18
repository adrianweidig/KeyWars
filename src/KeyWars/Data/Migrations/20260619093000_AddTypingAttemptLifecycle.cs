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
    [Migration("20260619093000_AddTypingAttemptLifecycle")]
    public partial class AddTypingAttemptLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "TypingAttempts",
                type: "TEXT",
                nullable: false,
                defaultValue: "Finished");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreparedAt",
                table: "TypingAttempts",
                type: "TEXT",
                nullable: false,
                defaultValue: DateTimeOffset.UnixEpoch);

            migrationBuilder.AddColumn<string>(
                name: "TextHash",
                table: "TypingAttempts",
                type: "TEXT",
                maxLength: 96,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ClientDurationMilliseconds",
                table: "TypingAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE TypingAttempts SET PreparedAt = StartedAt;");
            migrationBuilder.Sql("UPDATE TypingAttempts SET Phase = CASE WHEN FinishedAt IS NULL THEN 'Expired' ELSE 'Finished' END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientDurationMilliseconds",
                table: "TypingAttempts");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "TypingAttempts");

            migrationBuilder.DropColumn(
                name: "PreparedAt",
                table: "TypingAttempts");

            migrationBuilder.DropColumn(
                name: "TextHash",
                table: "TypingAttempts");
        }
    }
}
