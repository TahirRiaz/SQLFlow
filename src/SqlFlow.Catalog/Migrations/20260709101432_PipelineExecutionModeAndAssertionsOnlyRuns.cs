using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class PipelineExecutionModeAndAssertionsOnlyRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AssertionsOnly",
                schema: "catalog",
                table: "Run",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DataSetConvention",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            // Every pre-existing pipeline executes automatically (the pre-feature behavior), so the backfill
            // default is the auto spelling, not the empty string.
            migrationBuilder.AddColumn<string>(
                name: "ExecutionMode",
                schema: "catalog",
                table: "Pipeline",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: PipelineExecutionModes.Auto);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssertionsOnly",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "DataSetConvention",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "ExecutionMode",
                schema: "catalog",
                table: "Pipeline");
        }
    }
}
