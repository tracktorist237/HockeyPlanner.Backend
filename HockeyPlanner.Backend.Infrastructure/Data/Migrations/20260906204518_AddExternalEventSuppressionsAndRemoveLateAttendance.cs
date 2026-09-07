using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEventSuppressionsAndRemoveLateAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE attendances SET status = 2 WHERE status = 4;");
            migrationBuilder.Sql("UPDATE event_guests SET status = 2 WHERE status = 4;");

            migrationBuilder.CreateTable(
                name: "external_event_suppressions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_league_provider = table.Column<int>(type: "integer", nullable: false),
                    external_competition_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_match_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    external_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    competition_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_external_event_suppressions", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_event_suppressions_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_external_event_suppressions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_external_event_suppressions_created_by_user_id",
                table: "external_event_suppressions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_event_suppressions_identity",
                table: "external_event_suppressions",
                columns: new[] { "team_id", "external_league_provider", "external_competition_id", "external_match_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_event_suppressions");
        }
    }
}
