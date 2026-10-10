using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionExampleFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The confirmed-example store searched the same way the PowerBI-derived questions are, so one
            // search ranks both halves with one mechanism (POWERAI.md Section 6). Mirrors
            // AddQuestionFullTextSearch exactly: reuse the CatalogFullText catalog, create it only if that
            // migration's own guard skipped it, key on the table's primary key, and guard on the instance
            // having the Full-Text feature installed so this is a clean no-op where it is absent. Full-text
            // DDL cannot run inside a user transaction (suppressTransaction) and each statement must be alone
            // in its batch (EXEC).
            //
            // Separate from AddQuestionExamples rather than folded into it: the CREATE TABLE runs inside the
            // migration transaction while this runs outside it, so in one migration the OBJECT_ID guard below
            // could evaluate before the table was committed and silently skip the index it was meant to
            // create. A table migration followed by its index migration is also the shape the pair
            // AddSubscriberReportVisualQuestions/AddQuestionFullTextSearch already established.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'CatalogFullText') " +
                "EXEC('CREATE FULLTEXT CATALOG [CatalogFullText]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.QuestionExample')) " +
                "EXEC('CREATE FULLTEXT INDEX ON catalog.[QuestionExample]([Question]) KEY INDEX [PK_QuestionExample] ON [CatalogFullText] WITH CHANGE_TRACKING AUTO');",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only this migration's own index is dropped. The CatalogFullText catalog is left in place because
            // the object and question indexes still live in it; dropping it here would take those with it.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.QuestionExample')) " +
                "EXEC('DROP FULLTEXT INDEX ON catalog.[QuestionExample]');",
                suppressTransaction: true);
        }
    }
}
