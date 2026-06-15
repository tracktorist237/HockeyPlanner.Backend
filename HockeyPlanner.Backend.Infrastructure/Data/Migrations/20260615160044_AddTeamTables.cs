using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_tables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    template_type = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_team_tables", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_tables_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_tables_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "event_table_protocols",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_event_table_protocols", x => x.id);
                    table.ForeignKey(
                        name: "FK_event_table_protocols_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_table_protocols_team_tables_team_table_id",
                        column: x => x.team_table_id,
                        principalTable: "team_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_table_protocols_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "team_table_rows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    games = table.Column<int>(type: "integer", nullable: false),
                    goals = table.Column<int>(type: "integer", nullable: false),
                    assists = table.Column<int>(type: "integer", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_team_table_rows", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_table_rows_team_tables_team_table_id",
                        column: x => x.team_table_id,
                        principalTable: "team_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_table_rows_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_table_protocol_rows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_table_protocol_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    games = table.Column<int>(type: "integer", nullable: false),
                    goals = table.Column<int>(type: "integer", nullable: false),
                    assists = table.Column<int>(type: "integer", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_event_table_protocol_rows", x => x.id);
                    table.ForeignKey(
                        name: "FK_event_table_protocol_rows_event_table_protocols_event_table~",
                        column: x => x.event_table_protocol_id,
                        principalTable: "event_table_protocols",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_table_protocol_rows_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocol_rows_event_table_protocol_id",
                table: "event_table_protocol_rows",
                column: "event_table_protocol_id");

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocol_rows_event_table_protocol_id_user_id",
                table: "event_table_protocol_rows",
                columns: new[] { "event_table_protocol_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocol_rows_user_id",
                table: "event_table_protocol_rows",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocols_created_by_user_id",
                table: "event_table_protocols",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocols_event_id",
                table: "event_table_protocols",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocols_event_id_team_table_id",
                table: "event_table_protocols",
                columns: new[] { "event_id", "team_table_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_event_table_protocols_team_table_id",
                table: "event_table_protocols",
                column: "team_table_id");

            migrationBuilder.CreateIndex(
                name: "i_x_team_table_rows_team_table_id",
                table: "team_table_rows",
                column: "team_table_id");

            migrationBuilder.CreateIndex(
                name: "i_x_team_table_rows_team_table_id_user_id",
                table: "team_table_rows",
                columns: new[] { "team_table_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_team_table_rows_user_id",
                table: "team_table_rows",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_team_tables_created_at",
                table: "team_tables",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "i_x_team_tables_created_by_user_id",
                table: "team_tables",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_team_tables_team_id",
                table: "team_tables",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "i_x_team_tables_team_id_name",
                table: "team_tables",
                columns: new[] { "team_id", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_table_protocol_rows");

            migrationBuilder.DropTable(
                name: "team_table_rows");

            migrationBuilder.DropTable(
                name: "event_table_protocols");

            migrationBuilder.DropTable(
                name: "team_tables");
        }
    }
}
