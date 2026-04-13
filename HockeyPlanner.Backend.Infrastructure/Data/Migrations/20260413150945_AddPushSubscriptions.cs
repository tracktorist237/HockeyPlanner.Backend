using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPushSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    p256dh_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    auth_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    user_agent = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_birthday_notification_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_push_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_push_subscriptions_endpoint",
                table: "push_subscriptions",
                column: "endpoint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_subscriptions");
        }
    }
}
