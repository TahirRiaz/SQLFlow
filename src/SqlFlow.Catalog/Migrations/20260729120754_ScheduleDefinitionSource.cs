using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleDefinitionSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefinitionFlow",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionPath",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionYaml",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefinitionFlow",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "DefinitionPath",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "DefinitionYaml",
                schema: "catalog",
                table: "Schedule");
        }
    }
}
