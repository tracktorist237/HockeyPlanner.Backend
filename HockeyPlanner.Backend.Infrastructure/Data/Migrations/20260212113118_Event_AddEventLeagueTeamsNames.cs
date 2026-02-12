using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Event_AddEventLeagueTeamsNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "away_team_name",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "home_team_name",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "league_name",
                table: "events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "away_team_name",
                table: "events");

            migrationBuilder.DropColumn(
                name: "home_team_name",
                table: "events");

            migrationBuilder.DropColumn(
                name: "league_name",
                table: "events");
        }
    }
}
