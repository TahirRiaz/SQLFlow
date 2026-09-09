using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class NodePool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Pool",
                schema: "catalog",
                table: "Node",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Node_Pool",
                schema: "catalog",
                table: "Node",
                column: "Pool");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Node_Pool",
                schema: "catalog",
                table: "Node");

            migrationBuilder.DropColumn(
                name: "Pool",
                schema: "catalog",
                table: "Node");
        }
    }
}
