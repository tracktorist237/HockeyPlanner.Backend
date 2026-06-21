using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDurationMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "duration_minutes",
                table: "events",
                type: "integer",
                nullable: false,
                defaultValue: 75);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "duration_minutes",
                table: "events");
        }
    }
}
