using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RepoSourceRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepoSource",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RemoteUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SyncIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    NextSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncedSha = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepoSource", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepoSource_Name",
                schema: "catalog",
                table: "RepoSource",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepoSource_NextSyncUtc",
                schema: "catalog",
                table: "RepoSource",
                column: "NextSyncUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepoSource",
                schema: "catalog");
        }
    }
}
