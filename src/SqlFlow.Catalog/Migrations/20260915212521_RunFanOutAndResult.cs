using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunFanOutAndResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run");

            migrationBuilder.AddColumn<int>(
                name: "FanOutCount",
                schema: "catalog",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FanOutRoot",
                schema: "catalog",
                table: "Run",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FanOutSlot",
                schema: "catalog",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Run_FanOutRoot_Status",
                schema: "catalog",
                table: "Run",
                columns: new[] { "FanOutRoot", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run",
                column: "PipelineId",
                unique: true,
                filter: "[Status] = 'running' AND [FanOutRoot] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Run_FanOutRoot_Status",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "FanOutCount",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "FanOutRoot",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "FanOutSlot",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                schema: "catalog",
                table: "Run");

            migrationBuilder.CreateIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run",
                column: "PipelineId",
                unique: true,
                filter: "[Status] = 'running'");
        }
    }
}
