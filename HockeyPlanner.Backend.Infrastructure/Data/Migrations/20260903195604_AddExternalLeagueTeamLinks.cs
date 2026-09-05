using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalLeagueTeamLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_external_league_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    external_team_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_team_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    division_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    profile_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    logo_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cover_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    city = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    last_sync_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_successful_sync_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_team_external_league_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_external_league_links_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_team_external_league_links_provider_external_team_id",
                table: "team_external_league_links",
                columns: new[] { "provider", "external_team_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_team_external_league_links_team_id",
                table: "team_external_league_links",
                column: "team_id");

            migrationBuilder.Sql(
                """
                INSERT INTO team_external_league_links
                    (id, team_id, provider, external_team_id, external_team_name,
                     is_primary, last_sync_attempt_at, last_successful_sync_at,
                     created_at, updated_at)
                SELECT
                    id, id, 1, spbhl_team_id::text, COALESCE(spbhl_team_name, ''),
                    TRUE, spbhl_last_sync_attempt_at, spbhl_last_successful_sync_at,
                    created_at, NULL
                FROM teams
                WHERE spbhl_team_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_external_league_links");
        }
    }
}
