using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Unit coverage for the dataset-partitioned upsert (legacy DataSetColumn loop). These assert the SHAPE of the
/// generated script: one combined statement that dedups staging per (dataset, key), numbers the datasets in
/// ascending order, and walks them in a WHILE loop so a key recurring across datasets ends at the last dataset
/// that carries it. The row-level ordering behavior itself is proven against a real sink in
/// <see cref="Integration.DataSetLoopUpsertIntegrationTests"/>.
/// </summary>
public sealed class DataSetLoopGeneratorTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static UpsertOptions Options(
        IReadOnlyList<string> data,
        IReadOnlyList<string> keys,
        string dataSetColumn,
        bool batch = false,
        bool skipUpdate = false,
        bool skipInsert = false)
        => new()
        {
            DataColumns = data,
            KeyColumns = keys,
            DataSetColumn = dataSetColumn,
            BatchToAvoidLockEscalation = batch,
            BatchRowCount = 500,
            SkipUpdate = skipUpdate,
            SkipInsert = skipInsert,
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
            RowStatusColumn = "RowStatus_DW",
        };

    private static UpsertStatement Single(UpsertOptions options)
        => Assert.Single(UpsertGenerator.GenerateStatements(Obj("Trg"), Obj("Stg"), options));

    [Fact]
    public void DataSetLoop_ReturnsOneCombinedResultSetStatement()
    {
        var statement = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds"));

        Assert.Equal(UpsertStatementKind.Combined, statement.Kind);
        Assert.True(statement.CountFromResultSet);
        Assert.False(statement.CountFromScalar);
        Assert.EndsWith("FROM #InsertUpdates;", statement.Sql.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("AS Inserts", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("AS Updates", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_DedupsStagingPerDatasetAndKey()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds")).Sql;

        // One row per (dataset, key): the ROW_NUMBER partition is the dataset column plus the keys.
        Assert.Contains("PARTITION BY [Ds], [Id]", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE d._dsrn = 1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_OrdersDatasetsAscending()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds")).Sql;

        // The datasets are numbered in ascending order and walked one at a time.
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY _ds) AS RN", sql, StringComparison.Ordinal);
        Assert.Contains("WHILE @Counter <= @dsCount", sql, StringComparison.Ordinal);
        Assert.Contains("ds.RN = @Counter", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_MatchesDatasetNullSafe()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds")).Sql;

        // A NULL dataset value is its own partition, so the join is NULL-safe.
        Assert.Contains("src.[Ds] IS NULL AND ds._ds IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_InsertUsesLiveAntiJoin()
    {
        // The per-dataset insert anti-joins the LIVE target, so a key inserted by an earlier dataset is seen by
        // a later dataset (which then updates it instead of double-inserting): the ordered last-wins guarantee.
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds")).Sql;
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM [dbo].[Trg] AS trg WHERE src.[Id] = trg.[Id])", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataSetLoop_InsertStampsBothAuditColumns(bool batched)
    {
        // Legacy parity, same as the plain insert path: a row written by this loop carries the merge instant in
        // InsertedDate_DW AND UpdatedDate_DW. This loop is the path nearly every ported flow takes, because
        // DataSetColumn is the legacy standard, so omitting the update stamp here left UpdatedDate_DW NULL on
        // exactly the loads meant to reproduce old production row for row, and froze any watermark reading it.
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds", batch: batched)).Sql;

        var insert = sql.Split("INSERT INTO [dbo].[Trg] (")[1];
        var insertColumnList = insert[..insert.IndexOf(')')];
        Assert.Contains("[InsertedDate_DW]", insertColumnList, StringComparison.Ordinal);
        Assert.Contains("[UpdatedDate_DW]", insertColumnList, StringComparison.Ordinal);
        // One SYSUTCDATETIME() per stamped audit column, so both land with the same merge instant.
        var selectList = insert[(insert.IndexOf("SELECT ", StringComparison.Ordinal))..];
        selectList = selectList[..selectList.IndexOf("FROM #dsstg", StringComparison.Ordinal)];
        Assert.Equal(2, selectList.Split("SYSUTCDATETIME()").Length - 1);
    }

    [Fact]
    public void DataSetLoop_InsertOmitsUpdatedDate_WhenTheFlowDoesNotDeclareIt()
    {
        var options = Options(["Ds", "Id", "Name"], ["Id"], "Ds") with { UpdatedDateColumn = null };
        var sql = Single(options).Sql;

        Assert.DoesNotContain("[UpdatedDate_DW]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_ChangeDetectionUsesHashbytes()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds")).Sql;
        Assert.Contains("HASHBYTES", sql, StringComparison.Ordinal);
        Assert.Contains("[UpdatedDate_DW] = SYSUTCDATETIME()", sql, StringComparison.Ordinal);
        Assert.Contains("[RowStatus_DW] = 'U'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_Unbatched_HasNoKeyWindowTables()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds", batch: false)).Sql;
        Assert.DoesNotContain("#curUpd", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("#curIns", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@Batch", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_Batched_WindowsEachDatasetByKey()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds", batch: true)).Sql;
        Assert.Contains("#curUpd", sql, StringComparison.Ordinal);
        Assert.Contains("#curIns", sql, StringComparison.Ordinal);
        Assert.Contains("SET @Batch = 500;", sql, StringComparison.Ordinal);
        Assert.Contains("k.RowNum BETWEEN @s AND @e", sql, StringComparison.Ordinal);
        // The windowed DML is still scoped to the current dataset, so cross-dataset keys never leak in.
        Assert.Contains("ds.RN = @Counter", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_SkipUpdate_OmitsUpdateBranch()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds", skipUpdate: true)).Sql;
        Assert.DoesNotContain("UPDATE trg SET", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [dbo].[Trg]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetLoop_SkipInsert_OmitsInsertBranch()
    {
        var sql = Single(Options(["Ds", "Id", "Name"], ["Id"], "Ds", skipInsert: true)).Sql;
        Assert.Contains("UPDATE trg SET", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO [dbo].[Trg]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetColumn_AsKey_IsNotDuplicatedInPartition()
    {
        // When the dataset column is itself a key, the dedup partition lists it once.
        var sql = Single(Options(["Ds", "Name"], ["Ds"], "Ds")).Sql;
        Assert.Contains("PARTITION BY [Ds] ORDER BY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetColumn_NotAmongDataColumns_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(
            Obj("Trg"), Obj("Stg"),
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], DataSetColumn = "Ds" }));
        Assert.Contains("DataSetColumn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetColumn_WithScd2_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(
            Obj("Trg"), Obj("Stg"),
            new UpsertOptions
            {
                DataColumns = ["Ds", "Id", "Name"],
                KeyColumns = ["Id"],
                DataSetColumn = "Ds",
                Scd2Enabled = true,
                Scd2ValidFromColumn = "ValidFrom_DW",
                Scd2ValidToColumn = "ValidTo_DW",
                Scd2CurrentFlagColumn = "IsCurrent_DW",
                Scd2AsOfLiteral = "2026-01-01 00:00:00.000",
            }));
        Assert.Contains("SCD2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataSetColumn_WithNoKeys_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(
            Obj("Trg"), Obj("Stg"),
            new UpsertOptions { DataColumns = ["Ds", "Name"], KeyColumns = [], DataSetColumn = "Ds" }));

    [Fact]
    public void DataSetLoop_EscapesIdentifiers()
    {
        // A bracket in a column name is doubled, never breaking out of the quoting.
        var sql = Single(Options(["We[i]rd", "Id"], ["Id"], "We[i]rd")).Sql;
        Assert.Contains("[We[i]]rd]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[We[i]rd]", sql, StringComparison.Ordinal);
    }
}
