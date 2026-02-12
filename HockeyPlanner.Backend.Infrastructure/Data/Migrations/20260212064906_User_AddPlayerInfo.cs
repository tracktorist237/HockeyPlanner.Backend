using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPlanner.Backend.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class User_AddPlayerInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "birth_date",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "height",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "weight",
                table: "users",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "birth_date",
                table: "users");

            migrationBuilder.DropColumn(
                name: "height",
                table: "users");

            migrationBuilder.DropColumn(
                name: "weight",
                table: "users");
        }
    }
}
