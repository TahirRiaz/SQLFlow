using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPowerBiReportFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "VisualKey",
                schema: "catalog",
                table: "SubscriberReportVisual",
                type: "nvarchar(1192)",
                maxLength: 1192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(924)",
                oldMaxLength: 924);

            migrationBuilder.AlterColumn<string>(
                name: "PageKey",
                schema: "catalog",
                table: "SubscriberReportVisual",
                type: "nvarchar(1180)",
                maxLength: 1180,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(912)",
                oldMaxLength: 912);

            migrationBuilder.AlterColumn<string>(
                name: "PageKey",
                schema: "catalog",
                table: "SubscriberReportPage",
                type: "nvarchar(1180)",
                maxLength: 1180,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(912)",
                oldMaxLength: 912);

            migrationBuilder.AddColumn<string>(
                name: "ReportFile",
                schema: "catalog",
                table: "SubscriberReportPage",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "VisualKey",
                schema: "catalog",
                table: "SubscriberReportField",
                type: "nvarchar(1192)",
                maxLength: 1192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(924)",
                oldMaxLength: 924);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReportFile",
                schema: "catalog",
                table: "SubscriberReportPage");

            migrationBuilder.AlterColumn<string>(
                name: "VisualKey",
                schema: "catalog",
                table: "SubscriberReportVisual",
                type: "nvarchar(924)",
                maxLength: 924,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1192)",
                oldMaxLength: 1192);

            migrationBuilder.AlterColumn<string>(
                name: "PageKey",
                schema: "catalog",
                table: "SubscriberReportVisual",
                type: "nvarchar(912)",
                maxLength: 912,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1180)",
                oldMaxLength: 1180);

            migrationBuilder.AlterColumn<string>(
                name: "PageKey",
                schema: "catalog",
                table: "SubscriberReportPage",
                type: "nvarchar(912)",
                maxLength: 912,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1180)",
                oldMaxLength: 1180);

            migrationBuilder.AlterColumn<string>(
                name: "VisualKey",
                schema: "catalog",
                table: "SubscriberReportField",
                type: "nvarchar(924)",
                maxLength: 924,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1192)",
                oldMaxLength: 1192);
        }
    }
}
