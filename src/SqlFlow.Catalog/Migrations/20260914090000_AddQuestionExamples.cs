using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionExamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuestionExample",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Question = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Sql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ObjectKeys = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Confidence = table.Column<int>(type: "int", nullable: true),
                    ConfirmedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionExample", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuestionExample_ContentHash",
                schema: "catalog",
                table: "QuestionExample",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestionExample_Provenance",
                schema: "catalog",
                table: "QuestionExample",
                column: "Provenance");

            migrationBuilder.CreateIndex(
                name: "IX_QuestionExample_RepoId",
                schema: "catalog",
                table: "QuestionExample",
                column: "RepoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuestionExample",
                schema: "catalog");
        }
    }
}
