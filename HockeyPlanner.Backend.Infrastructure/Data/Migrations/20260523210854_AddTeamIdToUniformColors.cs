using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamIdToUniformColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "uniform_colors",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_uniform_colors_team_id",
                table: "uniform_colors",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "i_x_uniform_colors_team_id_name",
                table: "uniform_colors",
                columns: new[] { "team_id", "name" });

            migrationBuilder.AddForeignKey(
                name: "FK_uniform_colors_teams_team_id",
                table: "uniform_colors",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_uniform_colors_teams_team_id",
                table: "uniform_colors");

            migrationBuilder.DropIndex(
                name: "i_x_uniform_colors_team_id",
                table: "uniform_colors");

            migrationBuilder.DropIndex(
                name: "i_x_uniform_colors_team_id_name",
                table: "uniform_colors");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "uniform_colors");
        }
    }
}
