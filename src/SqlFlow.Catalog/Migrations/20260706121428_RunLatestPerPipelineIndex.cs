using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunLatestPerPipelineIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Run_PipelineId_WrittenUtc_RunId",
                schema: "catalog",
                table: "Run",
                columns: new[] { "PipelineId", "WrittenUtc", "RunId" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Run_PipelineId_WrittenUtc_RunId",
                schema: "catalog",
                table: "Run");
        }
    }
}
