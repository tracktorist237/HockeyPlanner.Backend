using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CascadeDeletingOfPlayersByLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_players_lines_line_id",
                table: "players");

            migrationBuilder.AddForeignKey(
                name: "FK_players_lines_line_id",
                table: "players",
                column: "line_id",
                principalTable: "lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_players_lines_line_id",
                table: "players");

            migrationBuilder.AddForeignKey(
                name: "FK_players_lines_line_id",
                table: "players",
                column: "line_id",
                principalTable: "lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
