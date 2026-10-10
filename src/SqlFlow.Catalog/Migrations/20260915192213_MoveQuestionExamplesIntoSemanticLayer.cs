using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class MoveQuestionExamplesIntoSemanticLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The saved answers become the semantic layer's example queries: catalog.QuestionExample is RENAMED to
            // catalog.SemanticExample, with its primary key and indexes, so every stored answer survives. The scaffolder
            // cannot tell a renamed entity from a new one and emitted a drop and a create, which would have deleted every
            // saved answer; this body replaces that.
            //
            // The full-text index (AddQuestionExampleFullTextSearch) is keyed on the primary key index, so it is dropped
            // before the rename and recreated on the renamed table afterwards. Full-text DDL cannot run inside a user
            // transaction, so every statement here runs outside one (suppressTransaction), in order, and each is guarded
            // so a partially applied run can be re-run without failing on a step that already happened.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.QuestionExample')) " +
                "EXEC('DROP FULLTEXT INDEX ON catalog.[QuestionExample]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF OBJECT_ID(N'catalog.QuestionExample') IS NOT NULL AND OBJECT_ID(N'catalog.SemanticExample') IS NULL " +
                "EXEC sp_rename N'catalog.QuestionExample', N'SemanticExample';",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF OBJECT_ID(N'catalog.PK_QuestionExample') IS NOT NULL " +
                "EXEC sp_rename N'catalog.PK_QuestionExample', N'PK_SemanticExample', N'OBJECT';",
                suppressTransaction: true);

            RenameIndex(migrationBuilder, "SemanticExample", "IX_QuestionExample_ContentHash", "IX_SemanticExample_ContentHash");
            RenameIndex(migrationBuilder, "SemanticExample", "IX_QuestionExample_Provenance", "IX_SemanticExample_Provenance");
            RenameIndex(migrationBuilder, "SemanticExample", "IX_QuestionExample_RepoId", "IX_SemanticExample_RepoId");

            CreateFullTextIndex(migrationBuilder, "SemanticExample", "PK_SemanticExample");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.SemanticExample')) " +
                "EXEC('DROP FULLTEXT INDEX ON catalog.[SemanticExample]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF OBJECT_ID(N'catalog.SemanticExample') IS NOT NULL AND OBJECT_ID(N'catalog.QuestionExample') IS NULL " +
                "EXEC sp_rename N'catalog.SemanticExample', N'QuestionExample';",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF OBJECT_ID(N'catalog.PK_SemanticExample') IS NOT NULL " +
                "EXEC sp_rename N'catalog.PK_SemanticExample', N'PK_QuestionExample', N'OBJECT';",
                suppressTransaction: true);

            RenameIndex(migrationBuilder, "QuestionExample", "IX_SemanticExample_ContentHash", "IX_QuestionExample_ContentHash");
            RenameIndex(migrationBuilder, "QuestionExample", "IX_SemanticExample_Provenance", "IX_QuestionExample_Provenance");
            RenameIndex(migrationBuilder, "QuestionExample", "IX_SemanticExample_RepoId", "IX_QuestionExample_RepoId");

            CreateFullTextIndex(migrationBuilder, "QuestionExample", "PK_QuestionExample");
        }

        /// <summary>Renames one index of <paramref name="table"/> when it still carries its old name.</summary>
        private static void RenameIndex(MigrationBuilder migrationBuilder, string table, string oldName, string newName)
            => migrationBuilder.Sql(
                $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'catalog.{table}') AND name = N'{oldName}') " +
                $"EXEC sp_rename N'catalog.{table}.{oldName}', N'{newName}', N'INDEX';",
                suppressTransaction: true);

        /// <summary>Creates the question full-text index on <paramref name="table"/>, exactly as
        /// AddQuestionExampleFullTextSearch created it, where the instance has the Full-Text feature and the index is
        /// not already there.</summary>
        private static void CreateFullTextIndex(MigrationBuilder migrationBuilder, string table, string keyIndex)
        {
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'CatalogFullText') " +
                "EXEC('CREATE FULLTEXT CATALOG [CatalogFullText]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                $"AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.{table}')) " +
                $"EXEC('CREATE FULLTEXT INDEX ON catalog.[{table}]([Question]) KEY INDEX [{keyIndex}] ON [CatalogFullText] WITH CHANGE_TRACKING AUTO');",
                suppressTransaction: true);
        }
    }
}
