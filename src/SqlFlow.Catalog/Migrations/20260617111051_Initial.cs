using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "Pipeline",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Batch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    RelativePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    SourceServer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    TargetServer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Yaml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipeline", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Repo",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RemoteUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    RootPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repo", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Run",
                schema: "catalog",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FlowKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationSeconds = table.Column<double>(type: "float", nullable: true),
                    RowsLoaded = table.Column<long>(type: "bigint", nullable: true),
                    RowsInserted = table.Column<long>(type: "bigint", nullable: true),
                    RowsUpdated = table.Column<long>(type: "bigint", nullable: true),
                    RowsDeleted = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Host = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Run", x => x.RunId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_Active",
                schema: "catalog",
                table: "Pipeline",
                column: "Active");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_Kind",
                schema: "catalog",
                table: "Pipeline",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_Name",
                schema: "catalog",
                table: "Pipeline",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_RepoId_Name",
                schema: "catalog",
                table: "Pipeline",
                columns: new[] { "RepoId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repo_Name",
                schema: "catalog",
                table: "Repo",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Run_FlowName",
                schema: "catalog",
                table: "Run",
                column: "FlowName");

            migrationBuilder.CreateIndex(
                name: "IX_Run_PipelineId",
                schema: "catalog",
                table: "Run",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_RepoId",
                schema: "catalog",
                table: "Run",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_WrittenUtc",
                schema: "catalog",
                table: "Run",
                column: "WrittenUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pipeline",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Repo",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Run",
                schema: "catalog");
        }
    }
}
