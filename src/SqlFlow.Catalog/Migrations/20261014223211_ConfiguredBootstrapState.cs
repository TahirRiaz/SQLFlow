using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ConfiguredBootstrapState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfiguredBy",
                schema: "catalog",
                table: "RepoSource",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfiguredBy",
                schema: "catalog",
                table: "AccessToken",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfiguredBy",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "ConfiguredBy",
                schema: "catalog",
                table: "AccessToken");
        }
    }
}
