using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamIdToEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_events_team_id",
                table: "events",
                column: "team_id");

            migrationBuilder.AddForeignKey(
                name: "FK_events_teams_team_id",
                table: "events",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_events_teams_team_id",
                table: "events");

            migrationBuilder.DropIndex(
                name: "i_x_events_team_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "events");
        }
    }
}
