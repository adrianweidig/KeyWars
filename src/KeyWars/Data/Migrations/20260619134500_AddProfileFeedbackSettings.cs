using KeyWars.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyWars.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(KeyWarsDbContext))]
    [Migration("20260619134500_AddProfileFeedbackSettings")]
    public partial class AddProfileFeedbackSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SoundVolumePercent",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 35);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoundVolumePercent",
                table: "UserProfiles");
        }
    }
}
