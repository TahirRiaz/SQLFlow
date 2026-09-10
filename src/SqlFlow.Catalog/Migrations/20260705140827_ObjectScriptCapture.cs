using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ObjectScriptCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tier",
                schema: "catalog",
                table: "ObjectColumn",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Script",
                schema: "catalog",
                table: "Object",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptTier",
                schema: "catalog",
                table: "Object",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScriptUpdatedUtc",
                schema: "catalog",
                table: "Object",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tier",
                schema: "catalog",
                table: "ObjectColumn");

            migrationBuilder.DropColumn(
                name: "Script",
                schema: "catalog",
                table: "Object");

            migrationBuilder.DropColumn(
                name: "ScriptTier",
                schema: "catalog",
                table: "Object");

            migrationBuilder.DropColumn(
                name: "ScriptUpdatedUtc",
                schema: "catalog",
                table: "Object");
        }
    }
}
