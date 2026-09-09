using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The SCD2 key-index migration: the canonical key index becomes a FILTERED unique index (one current row per
/// business key), and enabling SCD2 on an existing table must replace ANY non-filtered unique index on the key
/// (whatever its name), not only the canonical NCI_KeyColumn, or the old index would block a second version.
/// </summary>
public sealed class Scd2KeyIndexPlannerTests
{
    private static readonly RelationalObject Target = new() { Database = "DW", Schema = "dim", Name = "Customer" };

    [Fact]
    public void Plan_SuppressesThePlainUniqueKeyIndex_WhenScd2()
    {
        var statements = CanonicalIndexPlanner.Plan(
            Target, ["CustomerId"], dateColumn: null, dataSetColumn: null,
            hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false, scd2Enabled: true);

        // No plain unique NCI_KeyColumn under SCD2; the filtered one is created by the migration instead.
        Assert.DoesNotContain(statements, s => s.Contains("UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn]", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyIndexMigration_ReplacesAnyNonFilteredUniqueIndexOnTheKey()
    {
        var statements = CanonicalIndexPlanner.Scd2KeyIndexStatements(Target, ["CustomerId"], "IsCurrent_DW");
        var sql = string.Join("\n", statements);

        // (a) a wrongly-defined same-named index is dropped
        Assert.Contains("name = N'NCI_KeyColumn' AND NOT (is_unique = 1 AND has_filter = 1)", sql, StringComparison.Ordinal);
        // (b) ANY non-filtered unique nonclustered index on exactly the key (any name) is discovered and dropped,
        //     while primary keys and unique constraints are left alone
        Assert.Contains("is_unique = 1 AND i.has_filter = 0", sql, StringComparison.Ordinal);
        Assert.Contains("is_primary_key = 0 AND i.is_unique_constraint = 0", sql, StringComparison.Ordinal);
        Assert.Contains("c.name NOT IN (N'CustomerId')", sql, StringComparison.Ordinal);
        Assert.Contains("sp_executesql", sql, StringComparison.Ordinal);
        // (c) the canonical filtered-unique index is created
        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON [dim].[Customer] ([CustomerId]) WHERE [IsCurrent_DW] = 1;", sql, StringComparison.Ordinal);
    }
}
