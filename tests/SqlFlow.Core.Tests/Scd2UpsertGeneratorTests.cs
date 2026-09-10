using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The SCD Type 2 load generation: three ordered statements (backfill pre-existing rows current, close the
/// changed current rows, insert the new current versions), one consistent as-of instant for contiguous
/// periods, and one version per key. Pure text generation, asserted without a database.
/// </summary>
public sealed class Scd2UpsertGeneratorTests
{
    private const string AsOf = "2026-06-16 11:22:33.444";
    private const string Open = "9999-12-31 23:59:59.999";

    private static readonly RelationalObject Target = new() { Database = "DW", Schema = "dim", Name = "Customer" };
    private static readonly RelationalObject Staging = new() { Database = "DW", Schema = "dim", Name = "stg_Customer" };

    private static UpsertOptions Options(IReadOnlyList<string>? tracked = null, bool insertedDate = true) => new()
    {
        DataColumns = ["CustomerId", "Name", "City"],
        KeyColumns = ["CustomerId"],
        Scd2Enabled = true,
        Scd2ValidFromColumn = "ValidFrom_DW",
        Scd2ValidToColumn = "ValidTo_DW",
        Scd2CurrentFlagColumn = "IsCurrent_DW",
        Scd2TrackedColumns = tracked ?? [],
        Scd2AsOfLiteral = AsOf,
        InsertedDateColumn = insertedDate ? "InsertedDate_DW" : null,
        UpdatedDateColumn = "UpdatedDate_DW",
        RowStatusColumn = "RowStatus_DW",
    };

    [Fact]
    public void Emits_Backfill_Close_Insert_InOrder()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options());

        Assert.Equal(3, statements.Count);
        Assert.Equal(UpsertStatementKind.Update, statements[0].Kind); // backfill
        Assert.Equal(UpsertStatementKind.Update, statements[1].Kind); // close
        Assert.Equal(UpsertStatementKind.Insert, statements[2].Kind); // insert
    }

    [Fact]
    public void Backfill_StampsPreexistingRowsCurrent()
    {
        var backfill = UpsertGenerator.GenerateStatements(Target, Staging, Options())[0].Sql;

        // Only rows missing a period (predating SCD2) are touched, and they become the current version.
        Assert.Contains("WHERE trg.[ValidTo_DW] IS NULL", backfill, StringComparison.Ordinal);
        Assert.Contains("trg.[IsCurrent_DW] = 1", backfill, StringComparison.Ordinal);
        Assert.Contains($"trg.[ValidTo_DW] = '{Open}'", backfill, StringComparison.Ordinal);
        // Effective-from falls back through InsertedDate then the historical epoch.
        Assert.Contains("COALESCE(trg.[ValidFrom_DW], trg.[InsertedDate_DW], '1900-01-01 00:00:00.000')", backfill, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_ExpiresOnlyChangedCurrentRows()
    {
        var close = UpsertGenerator.GenerateStatements(Target, Staging, Options())[1].Sql;

        Assert.Contains("trg.[IsCurrent_DW] = 1", close, StringComparison.Ordinal);          // only the current row
        Assert.Contains("HASHBYTES", close, StringComparison.Ordinal);                        // and only if changed
        Assert.Contains("src.[Name]", close, StringComparison.Ordinal);
        Assert.Contains("src.[City]", close, StringComparison.Ordinal);
        Assert.DoesNotContain("src.[CustomerId]", close[(close.IndexOf("HASHBYTES", StringComparison.Ordinal))..], StringComparison.Ordinal); // key not tracked
        Assert.Contains($"trg.[ValidTo_DW] = '{AsOf}'", close, StringComparison.Ordinal);
        Assert.Contains("trg.[IsCurrent_DW] = 0", close, StringComparison.Ordinal);
        Assert.Contains($"trg.[UpdatedDate_DW] = '{AsOf}'", close, StringComparison.Ordinal);
        Assert.Contains("trg.[RowStatus_DW] = 'U'", close, StringComparison.Ordinal); // the expired version is auditable as touched
    }

    [Fact]
    public void Insert_AddsOneNewVersionPerKey_AntiJoinedOnCurrent()
    {
        var insert = UpsertGenerator.GenerateStatements(Target, Staging, Options())[2].Sql;

        // One row per key (dedupe), and only where there is no current row (changed keys were just closed; new
        // keys never had one).
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [CustomerId]", insert, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1", insert, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", insert, StringComparison.Ordinal);
        Assert.Contains("trg.[IsCurrent_DW] = 1", insert, StringComparison.Ordinal);
        // The new version is current, with contiguous from = the same as-of the close used.
        Assert.Contains("[ValidFrom_DW], [ValidTo_DW], [IsCurrent_DW]", insert, StringComparison.Ordinal);
        Assert.Contains($"'{AsOf}', '{Open}', 1", insert, StringComparison.Ordinal);
        Assert.Contains("'I'", insert, StringComparison.Ordinal); // RowStatus on the new version
    }

    [Fact]
    public void PeriodsAreContiguous_CloseValidToEqualsInsertValidFrom()
    {
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, Options());
        // The closed row's ValidTo and the new row's ValidFrom both use the identical as-of literal.
        Assert.Contains($"trg.[ValidTo_DW] = '{AsOf}'", statements[1].Sql, StringComparison.Ordinal);
        Assert.Contains($"'{AsOf}', '{Open}', 1", statements[2].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NoComparableAttributes_SkipsTheCloseStep()
    {
        // A key-only dimension has no non-key attribute to compare, so nothing is ever versioned: only the
        // backfill and the insert of brand-new keys remain.
        var keyOnly = Options() with { DataColumns = ["CustomerId"] };
        var statements = UpsertGenerator.GenerateStatements(Target, Staging, keyOnly);

        Assert.Equal(2, statements.Count);
        Assert.Equal(UpsertStatementKind.Update, statements[0].Kind); // backfill
        Assert.Equal(UpsertStatementKind.Insert, statements[1].Kind); // insert
    }

    [Fact]
    public void TrackedColumns_RestrictTheChangeDetection()
    {
        // Only City is tracked: a Name change must NOT open a new version.
        var close = UpsertGenerator.GenerateStatements(Target, Staging, Options(tracked: ["City"]))[1].Sql;
        var predicate = close[close.IndexOf("HASHBYTES", StringComparison.Ordinal)..];
        Assert.Contains("src.[City]", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("src.[Name]", predicate, StringComparison.Ordinal);
    }
}
