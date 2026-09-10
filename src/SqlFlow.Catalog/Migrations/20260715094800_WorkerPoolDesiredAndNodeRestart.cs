using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class WorkerPoolDesiredAndNodeRestart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RestartRequestedUtc",
                schema: "catalog",
                table: "Node",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkerPool",
                schema: "catalog",
                columns: table => new
                {
                    Pool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MinReplicas = table.Column<int>(type: "int", nullable: false),
                    ManualReplicas = table.Column<int>(type: "int", nullable: false),
                    ManualUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerPool", x => x.Pool);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerPool",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "RestartRequestedUtc",
                schema: "catalog",
                table: "Node");
        }
    }
}
