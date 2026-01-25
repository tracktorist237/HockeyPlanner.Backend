using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Attendance_RenameFieldToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attendances_users_player_id",
                table: "attendances");

            migrationBuilder.RenameColumn(
                name: "player_id",
                table: "attendances",
                newName: "user_id");

            migrationBuilder.RenameIndex(
                name: "i_x_attendances_player_id",
                table: "attendances",
                newName: "i_x_attendances_user_id");

            migrationBuilder.RenameIndex(
                name: "i_x_attendances_event_id_player_id",
                table: "attendances",
                newName: "i_x_attendances_event_id_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_attendances_users_user_id",
                table: "attendances",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attendances_users_user_id",
                table: "attendances");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "attendances",
                newName: "player_id");

            migrationBuilder.RenameIndex(
                name: "i_x_attendances_user_id",
                table: "attendances",
                newName: "i_x_attendances_player_id");

            migrationBuilder.RenameIndex(
                name: "i_x_attendances_event_id_user_id",
                table: "attendances",
                newName: "i_x_attendances_event_id_player_id");

            migrationBuilder.AddForeignKey(
                name: "FK_attendances_users_player_id",
                table: "attendances",
                column: "player_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
