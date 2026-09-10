using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunSubstitutionParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BackfillFrom",
                schema: "catalog",
                table: "Run",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BackfillTo",
                schema: "catalog",
                table: "Run",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilePattern",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FullLoad",
                schema: "catalog",
                table: "Run",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackfillFrom",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "BackfillTo",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "FilePattern",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "FullLoad",
                schema: "catalog",
                table: "Run");
        }
    }
}
