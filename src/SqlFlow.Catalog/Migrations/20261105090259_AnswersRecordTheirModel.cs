using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AnswersRecordTheirModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "catalog",
                table: "ChatMessage",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelProvider",
                schema: "catalog",
                table: "ChatMessage",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Model",
                schema: "catalog",
                table: "SubscriberReportVisualQuestion");

            migrationBuilder.DropColumn(
                name: "Model",
                schema: "catalog",
                table: "ChatMessage");

            migrationBuilder.DropColumn(
                name: "ModelProvider",
                schema: "catalog",
                table: "ChatMessage");
        }
    }
}
