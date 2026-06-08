using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstructionArticles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instruction_articles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    title = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    content = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    image_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_instruction_articles", x => x.id);
                    table.ForeignKey(
                        name: "FK_instruction_articles_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_instruction_articles_created_by_user_id",
                table: "instruction_articles",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_instruction_articles_is_published",
                table: "instruction_articles",
                column: "is_published");

            migrationBuilder.CreateIndex(
                name: "i_x_instruction_articles_published_at",
                table: "instruction_articles",
                column: "published_at");

            migrationBuilder.CreateIndex(
                name: "i_x_instruction_articles_slug",
                table: "instruction_articles",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_instruction_articles_sort_order",
                table: "instruction_articles",
                column: "sort_order");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instruction_articles");
        }
    }
}
