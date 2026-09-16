using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Removes the file nodes no lineage edge references. A file node exists only because a flow declares the location,
    /// so one without an edge is residue of a declaration that changed (a folder renamed, a location now anchored at its
    /// document) from before the sync swept such nodes itself. Its columns go with it.
    /// </summary>
    public partial class SweepOrphanFileNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE c FROM [catalog].[ObjectColumn] AS c
                WHERE EXISTS (
                    SELECT 1 FROM [catalog].[Object] AS o
                    WHERE o.[Key] = c.[ObjectKey] AND o.[Kind] = N'File'
                      AND NOT EXISTS (SELECT 1 FROM [catalog].[LineageEdge] AS e WHERE e.[ObjectKey] = o.[Key]));
                """);
            migrationBuilder.Sql("""
                DELETE o FROM [catalog].[Object] AS o
                WHERE o.[Kind] = N'File'
                  AND NOT EXISTS (SELECT 1 FROM [catalog].[LineageEdge] AS e WHERE e.[ObjectKey] = o.[Key]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The removed rows named locations no flow declares any more; a later sync records every declared one again,
            // so there is nothing to restore.
        }
    }
}
