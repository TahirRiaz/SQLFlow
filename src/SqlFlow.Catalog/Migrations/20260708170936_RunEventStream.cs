using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunEventStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TimestampUtc",
                schema: "catalog",
                table: "RunStatement",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rows = table.Column<long>(type: "bigint", nullable: true),
                    ElapsedMs = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunEvent_RunId",
                schema: "catalog",
                table: "RunEvent",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunEvent",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "TimestampUtc",
                schema: "catalog",
                table: "RunStatement");
        }
    }
}
