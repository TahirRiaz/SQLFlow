using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ObjectDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyColumns",
                schema: "catalog",
                table: "Object",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyOrigin",
                schema: "catalog",
                table: "Object",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ObjectRelationship",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    FromObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    FromColumns = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ToObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ToColumns = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Tier = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectRelationship", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectRelationship_FromObjectKey",
                schema: "catalog",
                table: "ObjectRelationship",
                column: "FromObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectRelationship_RepoId",
                schema: "catalog",
                table: "ObjectRelationship",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectRelationship_ToObjectKey",
                schema: "catalog",
                table: "ObjectRelationship",
                column: "ToObjectKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjectRelationship",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "KeyColumns",
                schema: "catalog",
                table: "Object");

            migrationBuilder.DropColumn(
                name: "KeyOrigin",
                schema: "catalog",
                table: "Object");
        }
    }
}
