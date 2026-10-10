using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RenameColumnPolicyToAllowList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsSensitive",
                schema: "catalog",
                table: "ColumnPolicy",
                newName: "IsAllowed");

            // The rename above carries the raw bit value forward unchanged, but the meaning flips: a row that
            // was IsSensitive=1 (hidden) must become IsAllowed=0 (still hidden), and a row that was
            // IsSensitive=0 (visible) must become IsAllowed=1 (still visible), so every estate that already has
            // policy rows keeps exactly the visibility it had a moment ago.
            migrationBuilder.Sql(
                "UPDATE [catalog].[ColumnPolicy] SET [IsAllowed] = CASE WHEN [IsAllowed] = 1 THEN 0 ELSE 1 END;");

            // The estate is moving from a blacklist (absence = visible) to a whitelist (absence = hidden), so
            // every column the catalog already knows about, that has no policy row yet, gets one seeded as
            // allowed here - this is what keeps every existing query/search/lineage surface working exactly as
            // it did before this migration. A column a future sync discovers for the first time gets no such
            // row and starts out denied until an admin reviews it; only columns present in this estate's own
            // catalog at the moment this migration runs are seeded, so this is correct per-environment rather
            // than copying one estate's shape onto another's.
            migrationBuilder.Sql(
                """
                INSERT INTO [catalog].[ColumnPolicy] ([ObjectKey], [ColumnName], [IsAllowed], [Reason], [UpdatedBy], [UpdatedUtc])
                SELECT oc.[ObjectKey], oc.[Name], 1, NULL, 'migration:RenameColumnPolicyToAllowList', SYSUTCDATETIME()
                FROM [catalog].[ObjectColumn] oc
                WHERE NOT EXISTS (
                    SELECT 1 FROM [catalog].[ColumnPolicy] cp
                    WHERE cp.[ObjectKey] = oc.[ObjectKey] AND cp.[ColumnName] = oc.[Name]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [catalog].[ColumnPolicy] SET [IsAllowed] = CASE WHEN [IsAllowed] = 1 THEN 0 ELSE 1 END;");

            migrationBuilder.RenameColumn(
                name: "IsAllowed",
                schema: "catalog",
                table: "ColumnPolicy",
                newName: "IsSensitive");
        }
    }
}
