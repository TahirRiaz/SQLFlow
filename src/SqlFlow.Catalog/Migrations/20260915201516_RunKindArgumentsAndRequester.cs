using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunKindArgumentsAndRequester : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Operation",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValuesJson",
                schema: "catalog",
                table: "Schedule",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValuesJson",
                schema: "catalog",
                table: "Run",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Operation",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "ValuesJson",
                schema: "catalog",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "Operation",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "Payload",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "RequestedBy",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "ValuesJson",
                schema: "catalog",
                table: "Run");
        }
    }
}
