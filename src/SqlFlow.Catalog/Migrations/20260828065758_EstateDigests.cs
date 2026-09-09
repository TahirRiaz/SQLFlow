using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class EstateDigests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DigestCursorEventId",
                schema: "catalog",
                table: "NotificationWatermark",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "DigestDueUtc",
                schema: "catalog",
                table: "NotificationWatermark",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DigestPeriodStartUtc",
                schema: "catalog",
                table: "NotificationWatermark",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationDigest",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    FlowCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CancelledCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    AssertionFailedCount = table.Column<int>(type: "int", nullable: false),
                    FirstEventId = table.Column<long>(type: "bigint", nullable: false),
                    LastEventId = table.Column<long>(type: "bigint", nullable: false),
                    Truncated = table.Column<bool>(type: "bit", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SlackBlocksJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GroupsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDigest", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDigest_GeneratedUtc",
                schema: "catalog",
                table: "NotificationDigest",
                column: "GeneratedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDigest_Origin_PeriodEndUtc",
                schema: "catalog",
                table: "NotificationDigest",
                columns: new[] { "Origin", "PeriodEndUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDigest",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "DigestCursorEventId",
                schema: "catalog",
                table: "NotificationWatermark");

            migrationBuilder.DropColumn(
                name: "DigestDueUtc",
                schema: "catalog",
                table: "NotificationWatermark");

            migrationBuilder.DropColumn(
                name: "DigestPeriodStartUtc",
                schema: "catalog",
                table: "NotificationWatermark");
        }
    }
}
