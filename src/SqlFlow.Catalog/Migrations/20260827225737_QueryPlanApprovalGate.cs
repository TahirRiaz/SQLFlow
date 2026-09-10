using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class QueryPlanApprovalGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QueryPlan",
                schema: "catalog",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ProviderKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Database = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TargetPool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MaxRows = table.Column<int>(type: "int", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    PreparedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PreparedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueryPlan", x => x.PlanId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueryPlan_ExpiresUtc",
                schema: "catalog",
                table: "QueryPlan",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_QueryPlan_PreparedUtc",
                schema: "catalog",
                table: "QueryPlan",
                column: "PreparedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueryPlan",
                schema: "catalog");
        }
    }
}
