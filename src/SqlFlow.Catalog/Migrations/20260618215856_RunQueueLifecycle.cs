using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunQueueLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedByNode",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnqueuedUtc",
                schema: "catalog",
                table: "Run",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            // Every run that already exists is finished, so derive its lifecycle state from its recorded outcome
            // (the new column's "" default is only a placeholder for the NOT NULL backfill; real inserts always set
            // Status explicitly).
            migrationBuilder.Sql(
                "UPDATE [catalog].[Run] SET [Status] = CASE WHEN [Success] = 1 THEN 'succeeded' ELSE 'failed' END;");

            migrationBuilder.CreateIndex(
                name: "IX_Run_Status_EnqueuedUtc",
                schema: "catalog",
                table: "Run",
                columns: new[] { "Status", "EnqueuedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Run_Status_EnqueuedUtc",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "ClaimedByNode",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "EnqueuedUtc",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "catalog",
                table: "Run");
        }
    }
}
