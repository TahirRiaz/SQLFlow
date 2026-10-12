using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddAiContextFileText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedYaml",
                schema: "catalog",
                table: "AiContext",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentYaml",
                schema: "catalog",
                table: "AiContext",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedYaml",
                schema: "catalog",
                table: "AiContext");

            migrationBuilder.DropColumn(
                name: "CurrentYaml",
                schema: "catalog",
                table: "AiContext");
        }
    }
}
