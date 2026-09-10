using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class FlowVersionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FlowVersionHash",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FlowVersion",
                schema: "catalog",
                columns: table => new
                {
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Yaml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowVersion", x => x.ContentHash);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowVersion",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "FlowVersionHash",
                schema: "catalog",
                table: "Run");
        }
    }
}
