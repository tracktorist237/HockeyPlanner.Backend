using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseNoticesAndNotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    push_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    endpoint_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_push_subscriptions_push_subscriptio~",
                        column: x => x.push_subscription_id,
                        principalTable: "push_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "release_notices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    title = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    send_notification = table.Column<bool>(type: "boolean", nullable: false),
                    notification_sent = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_release_notices", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_notices_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_notification_deliveries_created_at",
                table: "notification_deliveries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "i_x_notification_deliveries_notification_id",
                table: "notification_deliveries",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "i_x_notification_deliveries_push_subscription_id",
                table: "notification_deliveries",
                column: "push_subscription_id");

            migrationBuilder.CreateIndex(
                name: "i_x_notification_deliveries_status",
                table: "notification_deliveries",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "i_x_notification_deliveries_user_id",
                table: "notification_deliveries",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_release_notices_created_by_user_id",
                table: "release_notices",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_release_notices_is_published",
                table: "release_notices",
                column: "is_published");

            migrationBuilder.CreateIndex(
                name: "i_x_release_notices_published_at",
                table: "release_notices",
                column: "published_at");

            migrationBuilder.CreateIndex(
                name: "i_x_release_notices_version",
                table: "release_notices",
                column: "version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "release_notices");
        }
    }
}
