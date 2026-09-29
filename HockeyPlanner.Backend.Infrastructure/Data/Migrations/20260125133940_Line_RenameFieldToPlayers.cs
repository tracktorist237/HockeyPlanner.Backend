using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Line_RenameFieldToPlayers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "first_name",
                table: "players",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "handedness",
                table: "players",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "jersey_number",
                table: "players",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_name",
                table: "players",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "first_name",
                table: "players");

            migrationBuilder.DropColumn(
                name: "handedness",
                table: "players");

            migrationBuilder.DropColumn(
                name: "jersey_number",
                table: "players");

            migrationBuilder.DropColumn(
                name: "last_name",
                table: "players");
        }
    }
}
