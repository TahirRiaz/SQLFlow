using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class LineageAndRunDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LineageEdge",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Flow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ViaModule = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: true),
                    Relation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ObjectName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Tier = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineageEdge", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Object",
                schema: "catalog",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ServerRef = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Database = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Schema = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Object", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "RunAssertion",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    AssertedValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Evaluated = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunAssertion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunFile",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Rows = table.Column<long>(type: "bigint", nullable: false),
                    Columns = table.Column<int>(type: "int", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunFile", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LineageEdge_ObjectKey",
                schema: "catalog",
                table: "LineageEdge",
                column: "ObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_LineageEdge_PipelineId",
                schema: "catalog",
                table: "LineageEdge",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_LineageEdge_RepoId",
                schema: "catalog",
                table: "LineageEdge",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_Object_Name",
                schema: "catalog",
                table: "Object",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Object_ServerRef",
                schema: "catalog",
                table: "Object",
                column: "ServerRef");

            migrationBuilder.CreateIndex(
                name: "IX_RunAssertion_RunId",
                schema: "catalog",
                table: "RunAssertion",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunFile_RunId",
                schema: "catalog",
                table: "RunFile",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineageEdge",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Object",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunAssertion",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunFile",
                schema: "catalog");
        }
    }
}
