using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEventRosterUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The operator preflight is a separate pre-merge gate. Recheck under
            // locks in this migration transaction to exclude writes during backfill.
            migrationBuilder.Sql("""
                LOCK TABLE events, lines, event_guests, players IN ACCESS EXCLUSIVE MODE;
                DO $hp84$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM players p
                        LEFT JOIN lines l ON l.id = p.line_id
                        LEFT JOIN events e ON e.id = l.event_id
                        LEFT JOIN event_guests g ON g.id = p.event_guest_id
                        WHERE e.id IS NULL OR
                            (p.event_guest_id IS NOT NULL AND g.event_id IS DISTINCT FROM l.event_id)
                    ) OR EXISTS (
                        SELECT 1 FROM players p JOIN lines l ON l.id = p.line_id
                        WHERE p.user_id IS NOT NULL
                        GROUP BY l.event_id, p.user_id HAVING COUNT(*) > 1
                    ) OR EXISTS (
                        SELECT 1 FROM players p JOIN lines l ON l.id = p.line_id
                        WHERE p.event_guest_id IS NOT NULL
                        GROUP BY l.event_id, p.event_guest_id HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'HP84 roster preflight failed; operator reconciliation is required. No data was removed.';
                    END IF;
                END $hp84$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_players_event_guests_event_guest_id",
                table: "players");

            migrationBuilder.DropForeignKey(
                name: "FK_players_lines_line_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "i_x_players_line_id_event_guest_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "i_x_players_line_id_user_id",
                table: "players");

            migrationBuilder.AddColumn<Guid>(
                name: "event_id",
                table: "players",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE players p SET event_id = l.event_id FROM lines l WHERE l.id = p.line_id;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "event_id", table: "players", type: "uuid", nullable: false,
                oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "a_k_lines_id_event_id",
                table: "lines",
                columns: new[] { "id", "event_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "a_k_event_guests_id_event_id",
                table: "event_guests",
                columns: new[] { "id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "i_x_players_event_guest_id_event_id",
                table: "players",
                columns: new[] { "event_guest_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "i_x_players_line_id_event_id",
                table: "players",
                columns: new[] { "line_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ux_players_event_guest",
                table: "players",
                columns: new[] { "event_id", "event_guest_id" },
                unique: true,
                filter: "event_guest_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_players_event_user",
                table: "players",
                columns: new[] { "event_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_players_event_guests_event_guest_id_event_id",
                table: "players",
                columns: new[] { "event_guest_id", "event_id" },
                principalTable: "event_guests",
                principalColumns: new[] { "id", "event_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_players_lines_line_id_event_id",
                table: "players",
                columns: new[] { "line_id", "event_id" },
                principalTable: "lines",
                principalColumns: new[] { "id", "event_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_players_event_guests_event_guest_id_event_id",
                table: "players");

            migrationBuilder.DropForeignKey(
                name: "FK_players_lines_line_id_event_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "i_x_players_event_guest_id_event_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "i_x_players_line_id_event_id",
                table: "players");

            migrationBuilder.DropIndex(
                name: "ux_players_event_guest",
                table: "players");

            migrationBuilder.DropIndex(
                name: "ux_players_event_user",
                table: "players");

            migrationBuilder.DropUniqueConstraint(
                name: "a_k_lines_id_event_id",
                table: "lines");

            migrationBuilder.DropUniqueConstraint(
                name: "a_k_event_guests_id_event_id",
                table: "event_guests");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "players");

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

            migrationBuilder.AddForeignKey(
                name: "FK_players_event_guests_event_guest_id",
                table: "players",
                column: "event_guest_id",
                principalTable: "event_guests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_players_lines_line_id",
                table: "players",
                column: "line_id",
                principalTable: "lines",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
