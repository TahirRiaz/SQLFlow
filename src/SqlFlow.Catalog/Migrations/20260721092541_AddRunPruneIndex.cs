using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddRunPruneIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Run_Status_WrittenUtc",
                schema: "catalog",
                table: "Run",
                columns: new[] { "Status", "WrittenUtc" })
                .Annotation("SqlServer:Include", new[] { "PipelineId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Run_Status_WrittenUtc",
                schema: "catalog",
                table: "Run");
        }
    }
}
