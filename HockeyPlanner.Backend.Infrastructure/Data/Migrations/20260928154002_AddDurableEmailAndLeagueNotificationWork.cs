using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableEmailAndLeagueNotificationWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "notification_id",
                table: "notification_jobs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "notification_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "protected_payload",
                table: "notification_jobs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "token_record_id",
                table: "notification_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "notification_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "league_notification_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    background = table.Column<bool>(type: "boolean", nullable: false),
                    changes_json = table.Column<string>(type: "jsonb", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_league_notification_batches", x => x.id);
                    table.ForeignKey(
                        name: "FK_league_notification_batches_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_notification_jobs_user_id",
                table: "notification_jobs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_notification_jobs_auth_token",
                table: "notification_jobs",
                columns: new[] { "kind", "token_record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_league_notification_batches_team_id",
                table: "league_notification_batches",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_league_notification_batches_pending",
                table: "league_notification_batches",
                column: "completed_at");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_jobs_users_user_id",
                table: "notification_jobs",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM notification_jobs WHERE notification_id IS NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade while auth email jobs exist; retain the additive schema during application rollback.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_notification_jobs_users_user_id",
                table: "notification_jobs");

            migrationBuilder.DropTable(
                name: "league_notification_batches");

            migrationBuilder.DropIndex(
                name: "i_x_notification_jobs_user_id",
                table: "notification_jobs");

            migrationBuilder.DropIndex(
                name: "ux_notification_jobs_auth_token",
                table: "notification_jobs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "notification_jobs");

            migrationBuilder.DropColumn(
                name: "protected_payload",
                table: "notification_jobs");

            migrationBuilder.DropColumn(
                name: "token_record_id",
                table: "notification_jobs");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "notification_jobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "notification_id",
                table: "notification_jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
