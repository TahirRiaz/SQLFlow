using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriberQuerySourceSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceSql",
                schema: "catalog",
                table: "SubscriberQuery",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranslationProblem",
                schema: "catalog",
                table: "SubscriberQuery",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceSql",
                schema: "catalog",
                table: "SubscriberQuery");

            migrationBuilder.DropColumn(
                name: "TranslationProblem",
                schema: "catalog",
                table: "SubscriberQuery");
        }
    }
}
