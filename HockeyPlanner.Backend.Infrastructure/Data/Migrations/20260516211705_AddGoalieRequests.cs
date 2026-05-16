using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGoalieRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "push_subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "goalie_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    needed_count = table.Column<int>(type: "integer", nullable: false),
                    visibility = table.Column<int>(type: "integer", nullable: false),
                    response_mode = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    price_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_goalie_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_goalie_requests_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_goalie_requests_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_goalie_requests_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "goalie_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    goalie_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goalie_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_goalie_applications", x => x.id);
                    table.ForeignKey(
                        name: "FK_goalie_applications_goalie_requests_goalie_request_id",
                        column: x => x.goalie_request_id,
                        principalTable: "goalie_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_goalie_applications_users_goalie_user_id",
                        column: x => x.goalie_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_push_subscriptions_user_id",
                table: "push_subscriptions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_applications_goalie_request_id",
                table: "goalie_applications",
                column: "goalie_request_id");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_applications_goalie_request_id_goalie_user_id",
                table: "goalie_applications",
                columns: new[] { "goalie_request_id", "goalie_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_applications_goalie_user_id",
                table: "goalie_applications",
                column: "goalie_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_applications_status",
                table: "goalie_applications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_requests_created_by_user_id",
                table: "goalie_requests",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_requests_event_id",
                table: "goalie_requests",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_requests_status",
                table: "goalie_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_requests_team_id",
                table: "goalie_requests",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "i_x_goalie_requests_visibility",
                table: "goalie_requests",
                column: "visibility");

            migrationBuilder.AddForeignKey(
                name: "FK_push_subscriptions_users_user_id",
                table: "push_subscriptions",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_push_subscriptions_users_user_id",
                table: "push_subscriptions");

            migrationBuilder.DropTable(
                name: "goalie_applications");

            migrationBuilder.DropTable(
                name: "goalie_requests");

            migrationBuilder.DropIndex(
                name: "i_x_push_subscriptions_user_id",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "push_subscriptions");
        }
    }
}
