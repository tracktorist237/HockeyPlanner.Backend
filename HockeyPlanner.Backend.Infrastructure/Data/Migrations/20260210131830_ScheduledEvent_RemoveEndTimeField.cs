using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledEvent_RemoveEndTimeField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_events_end_time",
                table: "events");

            migrationBuilder.DropIndex(
                name: "i_x_events_start_time_end_time",
                table: "events");

            migrationBuilder.DropColumn(
                name: "end_time",
                table: "events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "end_time",
                table: "events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "i_x_events_end_time",
                table: "events",
                column: "end_time");

            migrationBuilder.CreateIndex(
                name: "i_x_events_start_time_end_time",
                table: "events",
                columns: new[] { "start_time", "end_time" });
        }
    }
}
