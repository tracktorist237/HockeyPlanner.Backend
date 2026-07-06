using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPrivacySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_privacy_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_visibility = table.Column<int>(type: "integer", nullable: false),
                    phone_visibility = table.Column<int>(type: "integer", nullable: false),
                    birth_date_visibility = table.Column<int>(type: "integer", nullable: false),
                    physical_visibility = table.Column<int>(type: "integer", nullable: false),
                    hockey_profile_visibility = table.Column<int>(type: "integer", nullable: false),
                    spbhl_profile_visibility = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_user_privacy_settings", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_privacy_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_user_privacy_settings_user_id",
                table: "user_privacy_settings",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_privacy_settings");
        }
    }
}
