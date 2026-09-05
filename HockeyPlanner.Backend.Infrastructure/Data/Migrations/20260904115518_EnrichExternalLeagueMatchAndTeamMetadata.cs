using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnrichExternalLeagueMatchAndTeamMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "administrator_name",
                table: "team_external_league_links",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coach_name",
                table: "team_external_league_links",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "founded_year",
                table: "team_external_league_links",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phones_json",
                table: "team_external_league_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website_urls_json",
                table: "team_external_league_links",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "administrator_name",
                table: "team_external_league_links");

            migrationBuilder.DropColumn(
                name: "coach_name",
                table: "team_external_league_links");

            migrationBuilder.DropColumn(
                name: "founded_year",
                table: "team_external_league_links");

            migrationBuilder.DropColumn(
                name: "phones_json",
                table: "team_external_league_links");

            migrationBuilder.DropColumn(
                name: "website_urls_json",
                table: "team_external_league_links");
        }
    }
}
