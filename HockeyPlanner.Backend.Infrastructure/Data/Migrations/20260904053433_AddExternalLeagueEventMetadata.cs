using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalLeagueEventMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_events_team_id_spbhl_tournament_id_spbhl_match_id",
                table: "events");

            migrationBuilder.AddColumn<string>(
                name: "external_competition_id",
                table: "events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_division_name",
                table: "events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "external_last_synced_at",
                table: "events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "external_league_provider",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_match_id",
                table: "events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_match_url",
                table: "events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_tournament_name",
                table: "events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE events
                SET external_league_provider = 1,
                    external_competition_id = spbhl_tournament_id::text,
                    external_match_id = spbhl_match_id::text,
                    external_match_url = spbhl_match_url,
                    external_last_synced_at = spbhl_last_synced_at
                WHERE spbhl_tournament_id IS NOT NULL
                  AND spbhl_match_id IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_events_external_identity",
                table: "events",
                columns: new[] { "team_id", "external_league_provider", "external_competition_id", "external_match_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_external_identity",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_competition_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_division_name",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_last_synced_at",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_league_provider",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_match_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_match_url",
                table: "events");

            migrationBuilder.DropColumn(
                name: "external_tournament_name",
                table: "events");

            migrationBuilder.CreateIndex(
                name: "i_x_events_team_id_spbhl_tournament_id_spbhl_match_id",
                table: "events",
                columns: new[] { "team_id", "spbhl_tournament_id", "spbhl_match_id" },
                unique: true);
        }
    }
}
