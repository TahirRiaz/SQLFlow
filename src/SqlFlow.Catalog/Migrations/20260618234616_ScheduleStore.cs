using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Schedule",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Cron = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    Timezone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Paused = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NextFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedule", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_NextFireUtc",
                schema: "catalog",
                table: "Schedule",
                column: "NextFireUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_PipelineId",
                schema: "catalog",
                table: "Schedule",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RepoId",
                schema: "catalog",
                table: "Schedule",
                column: "RepoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Schedule",
                schema: "catalog");
        }
    }
}
