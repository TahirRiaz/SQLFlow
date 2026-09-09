using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RunGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                schema: "catalog",
                table: "Run",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupWave",
                schema: "catalog",
                table: "Run",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RunGroup",
                schema: "catalog",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Anchor = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MemberCount = table.Column<int>(type: "int", nullable: false),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EnqueuedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunGroup", x => x.GroupId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Run_GroupId_GroupWave_Status",
                schema: "catalog",
                table: "Run",
                columns: new[] { "GroupId", "GroupWave", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RunGroup_RepoId_EnqueuedUtc",
                schema: "catalog",
                table: "RunGroup",
                columns: new[] { "RepoId", "EnqueuedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunGroup",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_Run_GroupId_GroupWave_Status",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "catalog",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "GroupWave",
                schema: "catalog",
                table: "Run");
        }
    }
}
