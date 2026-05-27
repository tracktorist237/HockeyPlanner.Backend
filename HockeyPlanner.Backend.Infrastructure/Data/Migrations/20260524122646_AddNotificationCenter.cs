using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "device_name",
                table: "push_subscriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "push_subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_seen_at",
                table: "push_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform",
                table: "push_subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at",
                table: "push_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attendance_required_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    roster_ready_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    team_news_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    goalies_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    birthdays_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    app_updates_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_notification_preferences", x => x.id);
                    table.ForeignKey(
                        name: "FK_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    category = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_notification_preferences_user_id",
                table: "notification_preferences",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_notifications_user_id",
                table: "notifications",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_notifications_user_id_is_read_created_at",
                table: "notifications",
                columns: new[] { "user_id", "is_read", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropColumn(
                name: "device_name",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "platform",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "push_subscriptions");
        }
    }
}
