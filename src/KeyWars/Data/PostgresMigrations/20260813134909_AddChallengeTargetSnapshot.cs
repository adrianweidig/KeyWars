using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddChallengeTargetSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetTextHash",
                table: "Challenges",
                type: "character varying(71)",
                maxLength: 71,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetTextSnapshot",
                table: "Challenges",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetTextHash",
                table: "Challenges");

            migrationBuilder.DropColumn(
                name: "TargetTextSnapshot",
                table: "Challenges");
        }
    }
}
