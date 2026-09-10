using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ComputeTaskQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComputeTask",
                schema: "catalog",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ProviderKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetPool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EnqueuedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimedByNode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CancelRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeTask", x => x.TaskId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTask_EnqueuedUtc",
                schema: "catalog",
                table: "ComputeTask",
                column: "EnqueuedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTask_SourceRef",
                schema: "catalog",
                table: "ComputeTask",
                column: "SourceRef");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTask_Status_EnqueuedUtc",
                schema: "catalog",
                table: "ComputeTask",
                columns: new[] { "Status", "EnqueuedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputeTask",
                schema: "catalog");
        }
    }
}
