using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RepoSourceSyncProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncRequestedUtc",
                schema: "catalog",
                table: "RepoSource",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncStartedUtc",
                schema: "catalog",
                table: "RepoSource",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncRequestedUtc",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "SyncStartedUtc",
                schema: "catalog",
                table: "RepoSource");
        }
    }
}
