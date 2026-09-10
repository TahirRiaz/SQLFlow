using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ObjectColumnUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ObjectColumn_ObjectKey",
                schema: "catalog",
                table: "ObjectColumn");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectColumn_ObjectKey_Ordinal",
                schema: "catalog",
                table: "ObjectColumn",
                columns: new[] { "ObjectKey", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ObjectColumn_ObjectKey_Ordinal",
                schema: "catalog",
                table: "ObjectColumn");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectColumn_ObjectKey",
                schema: "catalog",
                table: "ObjectColumn",
                column: "ObjectKey");
        }
    }
}
