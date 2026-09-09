using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleChaining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AfterSchedule",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastParentFireUtc",
                schema: "catalog",
                table: "Schedule",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RepoId_AfterSchedule",
                schema: "catalog",
                table: "Schedule",
                columns: new[] { "RepoId", "AfterSchedule" },
                filter: "[AfterSchedule] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Schedule_RepoId_AfterSchedule",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "AfterSchedule",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "LastParentFireUtc",
                schema: "catalog",
                table: "Schedule");
        }
    }
}
