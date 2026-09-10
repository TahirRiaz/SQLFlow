using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The legacy-parity upsert upgrades: checksum exclusions, staged-row dedupe, system-column stamping
/// (RowStatus + InsertedDate backfill), and the batched key-window apply.</summary>
public sealed class UpsertGeneratorUpgradeTests
{
    private static readonly RelationalObject Target = new() { Database = "db", Schema = "dbo", Name = "Trg" };
    private static readonly RelationalObject Staging = new() { Database = "db", Schema = "dbo", Name = "Stg" };

    private static UpsertOptions Options() => new()
    {
        DataColumns = ["Id", "Name", "Amount"],
        KeyColumns = ["Id"],
    };

    [Fact]
    public void ExcludedColumn_LeavesChecksum_ButStaysInSet()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options() with
        {
            ExcludeFromChecksum = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Amount" },
        });

        var update = Assert.Single(statements, s => s.Kind == UpsertStatementKind.Update).Sql;
        Assert.Contains("trg.[Amount] = src.[Amount]", update, StringComparison.Ordinal);          // still copied
        Assert.Contains("src.[Name]", update[update.IndexOf("HASHBYTES", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("[Amount]", update[update.IndexOf("HASHBYTES", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void AllColumnsExcluded_DropsChangePredicate_UpdatesAllMatched()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options() with
        {
            ExcludeFromChecksum = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Amount" },
        });

        var update = Assert.Single(statements, s => s.Kind == UpsertStatementKind.Update).Sql;
        Assert.DoesNotContain("HASHBYTES", update, StringComparison.Ordinal);

        // No change predicate left, so the only filter is the per-key dedup of staging.
        Assert.EndsWith("WHERE src._rn = 1;", update, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_CollapsesStagingToOneRowPerKey()
    {
        // Staging can legitimately carry several rows with the same business key (an incremental read over an
        // append-mode landing source: the same key across multiple file loads). The keyed INSERT must add one
        // row per key or it violates the target's unique key, so it partitions by key and keeps _rn = 1 rather
        // than relying on a full-row SELECT DISTINCT (which same-key rows differing in a provenance column pass).
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options());
        var insert = Assert.Single(statements, s => s.Kind == UpsertStatementKind.Insert).Sql;
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [Id]", insert, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1 AND NOT EXISTS", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemColumns_StampBothBranches()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options() with
        {
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
            RowStatusColumn = "RowStatus_DW",
        });

        var update = Assert.Single(statements, s => s.Kind == UpsertStatementKind.Update).Sql;
        Assert.Contains("trg.[UpdatedDate_DW] = SYSUTCDATETIME()", update, StringComparison.Ordinal);
        Assert.Contains("trg.[RowStatus_DW] = 'U'", update, StringComparison.Ordinal);
        // The legacy InsertedDate NULL-backfill: stamped only when missing, never overwritten.
        Assert.Contains("CASE WHEN trg.[InsertedDate_DW] IS NULL THEN SYSUTCDATETIME() ELSE trg.[InsertedDate_DW] END", update, StringComparison.Ordinal);

        var insert = Assert.Single(statements, s => s.Kind == UpsertStatementKind.Insert).Sql;
        Assert.Contains("[InsertedDate_DW]", insert, StringComparison.Ordinal);
        Assert.Contains("[RowStatus_DW]", insert, StringComparison.Ordinal);
        Assert.Contains("'I'", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Batched_GeneratesKeyWindowScripts_WithScalarCounts()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options() with
        {
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 500,
        });

        Assert.Equal(2, statements.Count);
        Assert.All(statements, s => Assert.True(s.CountFromScalar));

        var update = statements.Single(s => s.Kind == UpsertStatementKind.Update).Sql;
        Assert.Contains("#UpsertKeysU", update, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER", update, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE CLUSTERED INDEX", update, StringComparison.Ordinal);
        Assert.Contains("WHILE @Start <= @Total", update, StringComparison.Ordinal);
        Assert.Contains("@End bigint = 500", update, StringComparison.Ordinal);
        Assert.Contains("HASHBYTES", update, StringComparison.Ordinal);            // change predicate inside the window
        Assert.Contains("SELECT @Affected;", update, StringComparison.Ordinal);

        var insert = statements.Single(s => s.Kind == UpsertStatementKind.Insert).Sql;
        Assert.Contains("#UpsertKeysI", insert, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS", insert, StringComparison.Ordinal);     // anti-join builds the key set
        Assert.Contains("SELECT @Affected;", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Batched_InvalidBatchSize_Throws()
    {
        Assert.Throws<SqlFlow.Core.SqlFlowException>(() => UpsertGenerator.GenerateStatements(
            Target, Staging, Options() with { BatchToAvoidLockEscalation = true, BatchRowCount = 0 }));
    }

    [Fact]
    public void Default_IsUnbatched_NoScalarCount()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options());
        Assert.All(statements, s => Assert.False(s.CountFromScalar));
        Assert.DoesNotContain(statements, s => s.Sql.Contains("#UpsertKeys", StringComparison.Ordinal));
    }
}
