using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// The Maintenance page describes the whole database area by area, so the single row that held the SQL statements'
    /// retention becomes a retention per storage area (<c>RetentionSetting</c>), and every cleanup is logged
    /// (<c>MaintenanceCleanup</c>): who asked, at which cut-off, how many rows went and how it ended. A retention an admin
    /// already set for the SQL statements moves to its area, <c>sqlflow.run-statements</c>, with who set it and when, before
    /// the old table goes; going down moves it back.
    /// </summary>
    public partial class MaintenanceStorageAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceCleanup",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AreaId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Host = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Days = table.Column<int>(type: "int", nullable: false),
                    OlderThanUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Removed = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceCleanup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionSetting",
                schema: "catalog",
                columns: table => new
                {
                    AreaId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Days = table.Column<int>(type: "int", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionSetting", x => x.AreaId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceCleanup_AreaId_StartedUtc",
                schema: "catalog",
                table: "MaintenanceCleanup",
                columns: new[] { "AreaId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceCleanup_StartedUtc",
                schema: "catalog",
                table: "MaintenanceCleanup",
                column: "StartedUtc");

            migrationBuilder.Sql("""
                INSERT INTO [catalog].[RetentionSetting] ([AreaId], [Days], [UpdatedUtc], [UpdatedBy])
                SELECT N'sqlflow.run-statements', [RunTraceRetentionDays], [UpdatedUtc], [UpdatedBy]
                FROM [catalog].[MaintenanceSetting]
                WHERE [Id] = 1;
                """);

            migrationBuilder.DropTable(
                name: "MaintenanceSetting",
                schema: "catalog");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceSetting",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RunTraceRetentionDays = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceSetting", x => x.Id);
                });

            migrationBuilder.Sql("""
                INSERT INTO [catalog].[MaintenanceSetting] ([Id], [RunTraceRetentionDays], [UpdatedUtc], [UpdatedBy])
                SELECT 1, [Days], [UpdatedUtc], [UpdatedBy]
                FROM [catalog].[RetentionSetting]
                WHERE [AreaId] = N'sqlflow.run-statements';
                """);

            migrationBuilder.DropTable(
                name: "MaintenanceCleanup",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RetentionSetting",
                schema: "catalog");
        }
    }
}
