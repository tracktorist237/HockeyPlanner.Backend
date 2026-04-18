using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUniformColorsDictionaryAndEventRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "uniform_color_image_url",
                table: "events");

            migrationBuilder.AddColumn<Guid>(
                name: "uniform_color_id",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "uniform_colors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    image_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_uniform_colors", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_events_uniform_color_id",
                table: "events",
                column: "uniform_color_id");

            migrationBuilder.CreateIndex(
                name: "i_x_uniform_colors_name",
                table: "uniform_colors",
                column: "name");

            migrationBuilder.AddForeignKey(
                name: "FK_events_uniform_colors_uniform_color_id",
                table: "events",
                column: "uniform_color_id",
                principalTable: "uniform_colors",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_events_uniform_colors_uniform_color_id",
                table: "events");

            migrationBuilder.DropTable(
                name: "uniform_colors");

            migrationBuilder.DropIndex(
                name: "i_x_events_uniform_color_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "uniform_color_id",
                table: "events");

            migrationBuilder.AddColumn<string>(
                name: "uniform_color_image_url",
                table: "events",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }
    }
}
