using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastGroupId",
                schema: "catalog",
                table: "Schedule",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "flow");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastGroupId",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "catalog",
                table: "Schedule");
        }
    }
}
