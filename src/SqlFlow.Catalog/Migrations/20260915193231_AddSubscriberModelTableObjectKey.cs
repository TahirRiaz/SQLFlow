using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriberModelTableObjectKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ObjectKey",
                schema: "catalog",
                table: "SubscriberModelTable",
                type: "nvarchar(900)",
                maxLength: 900,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriberModelTable_ObjectKey",
                schema: "catalog",
                table: "SubscriberModelTable",
                column: "ObjectKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubscriberModelTable_ObjectKey",
                schema: "catalog",
                table: "SubscriberModelTable");

            migrationBuilder.DropColumn(
                name: "ObjectKey",
                schema: "catalog",
                table: "SubscriberModelTable");
        }
    }
}
