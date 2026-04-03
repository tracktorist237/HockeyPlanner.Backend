using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExercisesBankAndEventExercises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exercises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    video_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_exercises", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_event_exercises",
                columns: table => new
                {
                    scheduled_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_scheduled_event_exercises", x => new { x.scheduled_event_id, x.exercise_id });
                    table.ForeignKey(
                        name: "FK_scheduled_event_exercises_events_scheduled_event_id",
                        column: x => x.scheduled_event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scheduled_event_exercises_exercises_exercise_id",
                        column: x => x.exercise_id,
                        principalTable: "exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_exercises_name",
                table: "exercises",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "i_x_scheduled_event_exercises_exercise_id",
                table: "scheduled_event_exercises",
                column: "exercise_id");

            migrationBuilder.CreateIndex(
                name: "i_x_scheduled_event_exercises_scheduled_event_id_order",
                table: "scheduled_event_exercises",
                columns: new[] { "scheduled_event_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_event_exercises");

            migrationBuilder.DropTable(
                name: "exercises");
        }
    }
}
