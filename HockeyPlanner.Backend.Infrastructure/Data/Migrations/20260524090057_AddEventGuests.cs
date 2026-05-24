using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventGuests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_players_line_id_user_id",
                table: "players");

            migrationBuilder.AlterColumn<Guid>(
                name: "user_id",
                table: "players",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "event_guest_id",
                table: "players",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_guests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    handedness = table.Column<int>(type: "integer", nullable: true),
                    jersey_number = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    responded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_event_guests", x => x.id);
                    table.ForeignKey(
                        name: "FK_event_guests_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_guests_users_invited_by_user_id",
                        column: x => x.invited_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_players_event_guest_id",
                table: "players",
                column: "event_guest_id");

            migrationBuilder.CreateIndex(
                name: "i_x_players_line_id_event_guest_id",
                table: "players",
                columns: new[] { "line_id", "event_guest_id" },
                unique: true,
                filter: "event_guest_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "i_x_players_line_id_user_id",
                table: "players",
                columns: new[] { "line_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "i_x_event_guests_event_id",
                table: "event_guests",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "i_x_event_guests_invited_by_user_id",
                table: "event_guests",
                column: "invited_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_players_event_guests_event_guest_id",
                table: "players",
                column: "event_guest_id",
                principalTable: "event_guests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_players_event_guests_event_guest_id",
                table: "players");

            migrationBuilder.DropTable(
                name: "event_guests");

            migrationBuilder.DropIndex(
                name: "i_x_players_event_guest_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "i_x_players_line_id_event_guest_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "i_x_players_line_id_user_id",
                table: "players");

            migrationBuilder.DropColumn(
                name: "event_guest_id",
                table: "players");

            migrationBuilder.AlterColumn<Guid>(
                name: "user_id",
                table: "players",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_players_line_id_user_id",
                table: "players",
                columns: new[] { "line_id", "user_id" },
                unique: true);
        }
    }
}
