using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class PipelineTransformColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineColumn",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SourceColumn = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Expression = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DataType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: true),
                    IsVirtual = table.Column<bool>(type: "bit", nullable: false),
                    ExcludeFromView = table.Column<bool>(type: "bit", nullable: false),
                    Converted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineColumn", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineColumn_ColumnName",
                schema: "catalog",
                table: "PipelineColumn",
                column: "ColumnName");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineColumn_PipelineId",
                schema: "catalog",
                table: "PipelineColumn",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineColumn_PipelineId_Kind_Ordinal",
                schema: "catalog",
                table: "PipelineColumn",
                columns: new[] { "PipelineId", "Kind", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineColumn_RepoId",
                schema: "catalog",
                table: "PipelineColumn",
                column: "RepoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineColumn",
                schema: "catalog");
        }
    }
}
