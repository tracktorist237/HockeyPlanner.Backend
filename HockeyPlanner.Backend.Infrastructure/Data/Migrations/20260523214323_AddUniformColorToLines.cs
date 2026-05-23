using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUniformColorToLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "uniform_color_id",
                table: "lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_lines_uniform_color_id",
                table: "lines",
                column: "uniform_color_id");

            migrationBuilder.AddForeignKey(
                name: "FK_lines_uniform_colors_uniform_color_id",
                table: "lines",
                column: "uniform_color_id",
                principalTable: "uniform_colors",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_lines_uniform_colors_uniform_color_id",
                table: "lines");

            migrationBuilder.DropIndex(
                name: "i_x_lines_uniform_color_id",
                table: "lines");

            migrationBuilder.DropColumn(
                name: "uniform_color_id",
                table: "lines");
        }
    }
}
