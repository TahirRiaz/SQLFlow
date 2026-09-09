using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Unit coverage for the per-file (per-dataset) full replace (<c>load.reloadColumn</c>). These assert the SHAPE
/// of the two generated statements: a set-based purge that deletes the target rows for the datasets present in
/// staging (NULL-safe), followed by an insert of the batch (deduped per key when keys are declared, insert-all
/// otherwise). The end-to-end row behaviour (a resend fully replacing its prior version, including dropped
/// records, while other files are untouched) is proven against a real sink in
/// <see cref="Integration.ReloadColumnIntegrationTests"/>.
/// </summary>
public sealed class ReloadColumnGeneratorTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static UpsertOptions Options(
        IReadOnlyList<string> data,
        IReadOnlyList<string> keys,
        string reloadColumn,
        bool skipInsert = false,
        bool insertedDate = false,
        bool rowStatus = false)
        => new()
        {
            DataColumns = data,
            KeyColumns = keys,
            ReloadColumn = reloadColumn,
            SkipInsert = skipInsert,
            InsertedDateColumn = insertedDate ? "InsertedDate_DW" : null,
            RowStatusColumn = rowStatus ? "RowStatus_DW" : null,
        };

    private static IReadOnlyList<UpsertStatement> Generate(UpsertOptions options)
        => UpsertGenerator.GenerateStatements(Obj("Trg"), Obj("Stg"), options);

    [Fact]
    public void Reload_EmitsPurgeThenInsert()
    {
        var statements = Generate(Options(["FileName_DW", "Id", "Name"], ["Id"], "FileName_DW"));

        Assert.Equal(2, statements.Count);
        Assert.Equal(UpsertStatementKind.Purge, statements[0].Kind);
        Assert.Equal(UpsertStatementKind.Insert, statements[1].Kind);
    }

    [Fact]
    public void Reload_PurgeDeletesTargetRowsPresentInStaging()
    {
        var purge = Generate(Options(["FileName_DW", "Id", "Name"], ["Id"], "FileName_DW"))[0].Sql;

        // Deletes from the target, scoped to the datasets present in staging via a correlated EXISTS.
        Assert.Contains("DELETE trg FROM [dbo].[Trg] AS trg", purge, StringComparison.Ordinal);
        Assert.Contains("WHERE EXISTS (SELECT 1 FROM [dbo].[Stg] AS src WHERE src.[FileName_DW] = trg.[FileName_DW])", purge, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_PurgeIsNullSafe()
    {
        // A plain equality join is NULL-safe by definition: NULL never equals NULL in SQL, so a target or staging
        // row with no file identity is never matched (and so never purged). Guard against a regression that
        // introduces an IS NULL OR ... clause that would purge identity-less rows.
        var purge = Generate(Options(["FileName_DW", "Id"], ["Id"], "FileName_DW"))[0].Sql;

        Assert.DoesNotContain("IS NULL", purge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reload_KeyedInsertCollapsesToOneRowPerKey()
    {
        var insert = Generate(Options(["FileName_DW", "Id", "Name"], ["Id"], "FileName_DW"))[1].Sql;

        // With keys, the insert dedups staging to one row per key (ROW_NUMBER window) so a key recurring across
        // the batch's files cannot violate the target's unique key.
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [Id]", insert, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1", insert, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [dbo].[Trg]", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_KeylessInsertsEveryStagedRow()
    {
        var statements = Generate(Options(["FileName_DW", "Value"], [], "FileName_DW"));
        var insert = statements[1].Sql;

        // No keys: insert every staged row directly from staging (no per-key dedup window).
        Assert.Contains("FROM [dbo].[Stg] AS src;", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("_rn", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_StampsSystemColumnsOnInsert()
    {
        var insert = Generate(Options(["FileName_DW", "Id"], ["Id"], "FileName_DW", insertedDate: true, rowStatus: true))[1].Sql;

        Assert.Contains("[InsertedDate_DW]", insert, StringComparison.Ordinal);
        Assert.Contains("SYSUTCDATETIME()", insert, StringComparison.Ordinal);
        Assert.Contains("[RowStatus_DW]", insert, StringComparison.Ordinal);
        Assert.Contains("'I'", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_SkipInsert_EmitsPurgeOnly()
    {
        var statements = Generate(Options(["FileName_DW", "Id"], ["Id"], "FileName_DW", skipInsert: true));

        var only = Assert.Single(statements);
        Assert.Equal(UpsertStatementKind.Purge, only.Kind);
    }

    [Fact]
    public void Reload_EscapesIdentifiers()
    {
        var purge = Generate(Options(["File]Name", "Id"], ["Id"], "File]Name"))[0].Sql;

        // A closing bracket in the column name is doubled so the DELETE stays well-formed.
        Assert.Contains("src.[File]]Name] = trg.[File]]Name]", purge, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_ColumnNotAmongDataColumns_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() =>
            Generate(Options(["Id", "Name"], ["Id"], "FileName_DW")));
        Assert.Contains("ReloadColumn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_WithScd2_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(Obj("Trg"), Obj("Stg"), new UpsertOptions
        {
            DataColumns = ["FileName_DW", "Id"],
            KeyColumns = ["Id"],
            ReloadColumn = "FileName_DW",
            Scd2Enabled = true,
            Scd2ValidFromColumn = "ValidFrom_DW",
            Scd2ValidToColumn = "ValidTo_DW",
            Scd2CurrentFlagColumn = "IsCurrent_DW",
            Scd2AsOfLiteral = "2026-01-01 00:00:00.000",
        }));
        Assert.Contains("SCD2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_WithDataSetColumn_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(Obj("Trg"), Obj("Stg"), new UpsertOptions
        {
            DataColumns = ["FileName_DW", "Ds", "Id"],
            KeyColumns = ["Id"],
            ReloadColumn = "FileName_DW",
            DataSetColumn = "Ds",
        }));
        Assert.Contains("dataset-upsert loop", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_NeedsNoKeyColumns()
    {
        // Unlike the plain upsert, a keyless reload is valid (the file is the unit of replacement).
        var statements = Generate(Options(["FileName_DW", "Value"], [], "FileName_DW"));
        Assert.Equal(2, statements.Count);
    }
}
