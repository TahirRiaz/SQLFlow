using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class PipelineLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every pre-existing pipeline is production: the default that keeps an already-alerting estate
            // alerting. Only an explicit YAML `lifecycle: development` opts a flow out on its next sync.
            migrationBuilder.AddColumn<string>(
                name: "Lifecycle",
                schema: "catalog",
                table: "Pipeline",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: PipelineLifecycles.Production);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Lifecycle",
                schema: "catalog",
                table: "Pipeline");
        }
    }
}
