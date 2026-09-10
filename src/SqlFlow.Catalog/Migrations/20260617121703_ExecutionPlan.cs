using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ExecutionPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Wave",
                schema: "catalog",
                table: "Pipeline",
                type: "int",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.CreateTable(
                name: "FlowDependency",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromFlow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ToFlow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FromPipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToPipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViaObjects = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowDependency", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_RepoId_Wave",
                schema: "catalog",
                table: "Pipeline",
                columns: new[] { "RepoId", "Wave" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowDependency_FromPipelineId",
                schema: "catalog",
                table: "FlowDependency",
                column: "FromPipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_FlowDependency_RepoId",
                schema: "catalog",
                table: "FlowDependency",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_FlowDependency_ToPipelineId",
                schema: "catalog",
                table: "FlowDependency",
                column: "ToPipelineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowDependency",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_Pipeline_RepoId_Wave",
                schema: "catalog",
                table: "Pipeline");

            migrationBuilder.DropColumn(
                name: "Wave",
                schema: "catalog",
                table: "Pipeline");
        }
    }
}
