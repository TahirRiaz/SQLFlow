using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "catalog",
                table: "ColumnPolicy",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Synonyms",
                schema: "catalog",
                table: "ColumnPolicy",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SemanticLayerSettings",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemanticLayerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SemanticMeasure",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemanticMeasure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SemanticObject",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Synonyms = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    KeyColumns = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemanticObject", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SemanticRelationship",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    FromColumns = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ToObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ToColumns = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    JoinType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IdentityHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemanticRelationship", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SemanticMeasure_Name",
                schema: "catalog",
                table: "SemanticMeasure",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SemanticMeasure_ObjectKey",
                schema: "catalog",
                table: "SemanticMeasure",
                column: "ObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticObject_ObjectKey",
                schema: "catalog",
                table: "SemanticObject",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SemanticRelationship_FromObjectKey",
                schema: "catalog",
                table: "SemanticRelationship",
                column: "FromObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticRelationship_IdentityHash",
                schema: "catalog",
                table: "SemanticRelationship",
                column: "IdentityHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SemanticRelationship_ToObjectKey",
                schema: "catalog",
                table: "SemanticRelationship",
                column: "ToObjectKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SemanticLayerSettings",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SemanticMeasure",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SemanticObject",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SemanticRelationship",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "catalog",
                table: "ColumnPolicy");

            migrationBuilder.DropColumn(
                name: "Synonyms",
                schema: "catalog",
                table: "ColumnPolicy");
        }
    }
}
