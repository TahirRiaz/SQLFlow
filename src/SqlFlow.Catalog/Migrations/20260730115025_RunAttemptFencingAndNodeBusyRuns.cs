using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunAttemptFencingAndNodeBusyRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempt",
                schema: "catalog",
                table: "Run",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusyRuns",
                schema: "catalog",
                table: "Node",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempt",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "BusyRuns",
                schema: "catalog",
                table: "Node");
        }
    }
}
