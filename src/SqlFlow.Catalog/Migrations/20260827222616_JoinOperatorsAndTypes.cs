using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class JoinOperatorsAndTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JoinTypes",
                schema: "catalog",
                table: "ObjectRelationship",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Operators",
                schema: "catalog",
                table: "ObjectRelationship",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinTypes",
                schema: "catalog",
                table: "ObjectRelationship");

            migrationBuilder.DropColumn(
                name: "Operators",
                schema: "catalog",
                table: "ObjectRelationship");
        }
    }
}
