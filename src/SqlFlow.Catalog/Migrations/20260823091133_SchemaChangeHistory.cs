using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class SchemaChangeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SchemaChange",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Database = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Schema = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ChangeType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaChange", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChange_RepoId_Database_OccurredUtc",
                schema: "catalog",
                table: "SchemaChange",
                columns: new[] { "RepoId", "Database", "OccurredUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChange_RepoId_Database_Schema_Name",
                schema: "catalog",
                table: "SchemaChange",
                columns: new[] { "RepoId", "Database", "Schema", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChange_RepoId_OccurredUtc",
                schema: "catalog",
                table: "SchemaChange",
                columns: new[] { "RepoId", "OccurredUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChange_RunId",
                schema: "catalog",
                table: "SchemaChange",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaChange",
                schema: "catalog");
        }
    }
}
