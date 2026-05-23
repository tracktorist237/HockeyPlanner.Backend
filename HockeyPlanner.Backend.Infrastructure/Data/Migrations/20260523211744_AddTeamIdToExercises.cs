using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamIdToExercises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "exercises",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_exercises_team_id",
                table: "exercises",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "i_x_exercises_team_id_name",
                table: "exercises",
                columns: new[] { "team_id", "name" });

            migrationBuilder.AddForeignKey(
                name: "FK_exercises_teams_team_id",
                table: "exercises",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_exercises_teams_team_id",
                table: "exercises");

            migrationBuilder.DropIndex(
                name: "i_x_exercises_team_id",
                table: "exercises");

            migrationBuilder.DropIndex(
                name: "i_x_exercises_team_id_name",
                table: "exercises");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "exercises");
        }
    }
}
