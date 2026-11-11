using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RepoSourceAutoSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every source registered before this column existed synced on its interval, and keeps doing so: the default
            // fills the rows already there with true. Every insert names the value, so the default reaches nothing else.
            migrationBuilder.AddColumn<bool>(
                name: "AutoSync",
                schema: "catalog",
                table: "RepoSource",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoSyncConfiguredBy",
                schema: "catalog",
                table: "RepoSource",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoSyncUpdatedBy",
                schema: "catalog",
                table: "RepoSource",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoSyncUpdatedUtc",
                schema: "catalog",
                table: "RepoSource",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MoveRequestedUtc",
                schema: "catalog",
                table: "RepoSource",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MoveToCommit",
                schema: "catalog",
                table: "RepoSource",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoSync",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "AutoSyncConfiguredBy",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "AutoSyncUpdatedBy",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "AutoSyncUpdatedUtc",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "MoveRequestedUtc",
                schema: "catalog",
                table: "RepoSource");

            migrationBuilder.DropColumn(
                name: "MoveToCommit",
                schema: "catalog",
                table: "RepoSource");
        }
    }
}
