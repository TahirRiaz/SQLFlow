using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriberReportVisualQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                schema: "catalog",
                table: "SubscriberReportVisual",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SubscriberReportVisualQuestion",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisualKey = table.Column<string>(type: "nvarchar(1192)", maxLength: 1192, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Question = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriberReportVisualQuestion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportVisualQuestion_RepoId",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberReportVisualQuestion_VisualKey",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                column: "VisualKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriberReportVisualQuestion",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                schema: "catalog",
                table: "SubscriberReportVisual");
        }
    }
}
