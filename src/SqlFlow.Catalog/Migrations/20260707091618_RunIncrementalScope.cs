using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunIncrementalScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IncrementalFilter",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncrementalMode",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncrementalWatermark",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncrementalWatermarkSource",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncrementalFilter",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "IncrementalMode",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "IncrementalWatermark",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "IncrementalWatermarkSource",
                schema: "catalog",
                table: "Run");
        }
    }
}
