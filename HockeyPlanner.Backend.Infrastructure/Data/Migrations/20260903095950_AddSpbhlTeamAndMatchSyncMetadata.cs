using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpbhlTeamAndMatchSyncMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "spbhl_last_successful_sync_at",
                table: "teams",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "spbhl_last_sync_attempt_at",
                table: "teams",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "spbhl_team_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "spbhl_team_name",
                table: "teams",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "away_score",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "home_score",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "spbhl_last_synced_at",
                table: "events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "spbhl_match_id",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "spbhl_match_url",
                table: "events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "spbhl_tournament_id",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_teams_spbhl_team_id",
                table: "teams",
                column: "spbhl_team_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_events_team_id_spbhl_tournament_id_spbhl_match_id",
                table: "events",
                columns: new[] { "team_id", "spbhl_tournament_id", "spbhl_match_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_teams_spbhl_team_id",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "i_x_events_team_id_spbhl_tournament_id_spbhl_match_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "spbhl_last_successful_sync_at",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "spbhl_last_sync_attempt_at",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "spbhl_team_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "spbhl_team_name",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "away_score",
                table: "events");

            migrationBuilder.DropColumn(
                name: "home_score",
                table: "events");

            migrationBuilder.DropColumn(
                name: "spbhl_last_synced_at",
                table: "events");

            migrationBuilder.DropColumn(
                name: "spbhl_match_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "spbhl_match_url",
                table: "events");

            migrationBuilder.DropColumn(
                name: "spbhl_tournament_id",
                table: "events");
        }
    }
}
