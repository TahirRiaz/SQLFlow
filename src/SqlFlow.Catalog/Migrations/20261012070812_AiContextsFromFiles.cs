using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AiContextsFromFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuestionGeneration",
                schema: "catalog",
                table: "SemanticLayerSettings");

            migrationBuilder.DropColumn(
                name: "QuestionGenerationUpdatedBy",
                schema: "catalog",
                table: "SemanticLayerSettings");

            migrationBuilder.DropColumn(
                name: "QuestionGenerationUpdatedUtc",
                schema: "catalog",
                table: "SemanticLayerSettings");

            migrationBuilder.AddColumn<bool>(
                name: "GenerateQuestions",
                schema: "catalog",
                table: "Subscriber",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticRelationship",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticObject",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticMeasure",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticExample",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiContext",
                schema: "catalog",
                table: "ColumnPolicy",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiContext",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CurrentJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CurrentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Problems = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SyncedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiContext", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SemanticRelationship_AiContext",
                schema: "catalog",
                table: "SemanticRelationship",
                column: "AiContext");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticObject_AiContext",
                schema: "catalog",
                table: "SemanticObject",
                column: "AiContext");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticMeasure_AiContext",
                schema: "catalog",
                table: "SemanticMeasure",
                column: "AiContext");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticExample_AiContext",
                schema: "catalog",
                table: "SemanticExample",
                column: "AiContext");

            migrationBuilder.CreateIndex(
                name: "IX_ColumnPolicy_AiContext",
                schema: "catalog",
                table: "ColumnPolicy",
                column: "AiContext");

            migrationBuilder.CreateIndex(
                name: "IX_AiContext_Name",
                schema: "catalog",
                table: "AiContext",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiContext_RepoId",
                schema: "catalog",
                table: "AiContext",
                column: "RepoId");

            // Everything starts off. What an assistant may use is now an AI context a file proposes and an admin
            // approves, so nothing written before is served any more. RenameColumnPolicyToAllowList opened every column
            // the catalog held at the time, with no content of its own: those rows go. A denied column says nothing a
            // missing row does not, now that closed is the default: those go too. What is left is what an admin opened
            // or described in the GUI; it stays, closed and marked as no context's, so it can be exported into a file.
            migrationBuilder.Sql(
                """
                DELETE FROM [catalog].[ColumnPolicy]
                WHERE [UpdatedBy] = 'migration:RenameColumnPolicyToAllowList'
                  AND [Description] IS NULL AND [Synonyms] IS NULL AND [Reason] IS NULL;
                """);
            migrationBuilder.Sql("DELETE FROM [catalog].[ColumnPolicy] WHERE [IsAllowed] = 0;");
            migrationBuilder.Sql("UPDATE [catalog].[ColumnPolicy] SET [IsAllowed] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The structure goes back; the allow-list entries Up removed or closed do not, since nothing records which
            // columns were open before. An admin opens them again after a downgrade.
            migrationBuilder.DropTable(
                name: "AiContext",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_SemanticRelationship_AiContext",
                schema: "catalog",
                table: "SemanticRelationship");

            migrationBuilder.DropIndex(
                name: "IX_SemanticObject_AiContext",
                schema: "catalog",
                table: "SemanticObject");

            migrationBuilder.DropIndex(
                name: "IX_SemanticMeasure_AiContext",
                schema: "catalog",
                table: "SemanticMeasure");

            migrationBuilder.DropIndex(
                name: "IX_SemanticExample_AiContext",
                schema: "catalog",
                table: "SemanticExample");

            migrationBuilder.DropIndex(
                name: "IX_ColumnPolicy_AiContext",
                schema: "catalog",
                table: "ColumnPolicy");

            migrationBuilder.DropColumn(
                name: "GenerateQuestions",
                schema: "catalog",
                table: "Subscriber");

            migrationBuilder.DropColumn(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticRelationship");

            migrationBuilder.DropColumn(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticObject");

            migrationBuilder.DropColumn(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticMeasure");

            migrationBuilder.DropColumn(
                name: "AiContext",
                schema: "catalog",
                table: "SemanticExample");

            migrationBuilder.DropColumn(
                name: "AiContext",
                schema: "catalog",
                table: "ColumnPolicy");

            migrationBuilder.AddColumn<bool>(
                name: "QuestionGeneration",
                schema: "catalog",
                table: "SemanticLayerSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuestionGenerationUpdatedBy",
                schema: "catalog",
                table: "SemanticLayerSettings",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuestionGenerationUpdatedUtc",
                schema: "catalog",
                table: "SemanticLayerSettings",
                type: "datetime2",
                nullable: true);
        }
    }
}
