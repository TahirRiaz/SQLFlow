using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class ObjectFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FullTextKey",
                schema: "catalog",
                table: "Object",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.CreateIndex(
                name: "IX_Object_FullTextKey",
                schema: "catalog",
                table: "Object",
                column: "FullTextKey",
                unique: true);

            // Full-text search over the searchable text columns: the object module bodies (Object.Definition,
            // the SP/view source) and the generated SQL trace (RunStatement.Sql). Guarded by the instance having
            // the Full-Text feature installed, so this is a clean no-op where it is absent. Run with
            // suppressTransaction because full-text DDL cannot execute inside a user transaction, and wrapped in
            // EXEC because each full-text catalog/index statement must be the only statement in its batch. Each
            // create is skipped when already present, so the migration is idempotent.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'CatalogFullText') " +
                "EXEC('CREATE FULLTEXT CATALOG [CatalogFullText]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.Object')) " +
                "EXEC('CREATE FULLTEXT INDEX ON catalog.[Object]([Definition]) KEY INDEX [IX_Object_FullTextKey] ON [CatalogFullText] WITH CHANGE_TRACKING AUTO');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.RunStatement')) " +
                "EXEC('CREATE FULLTEXT INDEX ON catalog.[RunStatement]([Sql]) KEY INDEX [PK_RunStatement] ON [CatalogFullText] WITH CHANGE_TRACKING AUTO');",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the full-text indexes (they key on IX_Object_FullTextKey / PK_RunStatement) and the catalog
            // BEFORE dropping the key index and column. Guarded + suppressTransaction, mirroring Up.
            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.Object')) " +
                "EXEC('DROP FULLTEXT INDEX ON catalog.[Object]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'catalog.RunStatement')) " +
                "EXEC('DROP FULLTEXT INDEX ON catalog.[RunStatement]');",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "IF SERVERPROPERTY('IsFullTextInstalled') = 1 " +
                "AND EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'CatalogFullText') " +
                "EXEC('DROP FULLTEXT CATALOG [CatalogFullText]');",
                suppressTransaction: true);

            migrationBuilder.DropIndex(
                name: "IX_Object_FullTextKey",
                schema: "catalog",
                table: "Object");

            migrationBuilder.DropColumn(
                name: "FullTextKey",
                schema: "catalog",
                table: "Object");
        }
    }
}
