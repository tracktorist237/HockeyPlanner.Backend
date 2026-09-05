using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowSharedExternalLeagueProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_teams_spbhl_team_id",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "i_x_team_external_league_links_provider_external_team_id",
                table: "team_external_league_links");

            migrationBuilder.CreateIndex(
                name: "i_x_teams_spbhl_team_id",
                table: "teams",
                column: "spbhl_team_id");

            migrationBuilder.CreateIndex(
                name: "i_x_team_external_league_links_team_id_provider_external_team_id",
                table: "team_external_league_links",
                columns: new[] { "team_id", "provider", "external_team_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_teams_spbhl_team_id",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "i_x_team_external_league_links_team_id_provider_external_team_id",
                table: "team_external_league_links");

            migrationBuilder.CreateIndex(
                name: "i_x_teams_spbhl_team_id",
                table: "teams",
                column: "spbhl_team_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_team_external_league_links_provider_external_team_id",
                table: "team_external_league_links",
                columns: new[] { "provider", "external_team_id" },
                unique: true);
        }
    }
}
