using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Full-text search over the stored business questions, so a typed question can be matched against
            // them by meaning-bearing words rather than exact string equality (POWERAI.md Section 6). Reuses the
            // CatalogFullText catalog created by ObjectFullTextSearch, creating it only if that migration's own
            // guard skipped it, and keys on the table's primary key exactly as the RunStatement index does.
            // Guarded by the instance having the Full-Text feature installed, so this is a clean no-op where it
            // is absent; run with suppressTransaction because full-text DDL cannot execute inside a user
            // transaction, and wrapped in EXEC because each full-text statement must be alone in its batch.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'CatalogFullText') " +
                "EXEC('CREATE FULLTEXT CATALOG [CatalogFullText]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.SubscriberReportVisualQuestion')) " +
                "EXEC('CREATE FULLTEXT INDEX ON catalog.[SubscriberReportVisualQuestion]([Question]) KEY INDEX [PK_SubscriberReportVisualQuestion] ON [CatalogFullText] WITH CHANGE_TRACKING AUTO');",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only this migration's own index is dropped. The CatalogFullText catalog is left in place because
            // ObjectFullTextSearch's indexes still live in it; dropping it here would take those with it.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.SubscriberReportVisualQuestion')) " +
                "EXEC('DROP FULLTEXT INDEX ON catalog.[SubscriberReportVisualQuestion]');",
                suppressTransaction: true);
        }
    }
}
