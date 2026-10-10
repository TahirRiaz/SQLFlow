using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColumnPolicy",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnPolicy", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnPolicy_ObjectKey_ColumnName",
                schema: "catalog",
                table: "ColumnPolicy",
                columns: new[] { "ObjectKey", "ColumnName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnPolicy",
                schema: "catalog");
        }
    }
}
