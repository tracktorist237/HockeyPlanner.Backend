using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamJerseyNumberRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_duplicate_jersey_numbers",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "blocked_jersey_numbers_json",
                table: "teams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "team_jersey_number",
                table: "team_memberships",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_duplicate_jersey_numbers",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "blocked_jersey_numbers_json",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "team_jersey_number",
                table: "team_memberships");
        }
    }
}
