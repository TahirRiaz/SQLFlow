using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddManualSubscriberQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "generated");

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedUtc",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origin",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");

            migrationBuilder.DropColumn(
                name: "UpdatedUtc",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");
        }
    }
}
