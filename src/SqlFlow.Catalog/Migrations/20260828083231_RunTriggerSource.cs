using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunTriggerSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TriggerScheduleId",
                schema: "catalog",
                table: "Run",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerSource",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerScheduleId",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "TriggerSource",
                schema: "catalog",
                table: "Run");
        }
    }
}
