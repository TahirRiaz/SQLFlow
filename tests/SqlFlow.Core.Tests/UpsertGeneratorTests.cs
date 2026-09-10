using System.Linq;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class UpsertGeneratorTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static UpsertOptions Options(IReadOnlyList<string> data, IReadOnlyList<string> keys, bool skipUpdate = false, bool skipInsert = false)
        => new() { DataColumns = data, KeyColumns = keys, SkipUpdate = skipUpdate, SkipInsert = skipInsert };

    /// <summary>
    /// Legacy parity, and the reason this is pinned: an INSERTED row must carry the merge timestamp in BOTH audit
    /// columns. Stamping UpdatedDate_DW only on UPDATE leaves it NULL on every freshly landed row, which silently
    /// starves anything reading it as "when did this row last change": MAX(UpdatedDate_DW) over an insert-only
    /// table never advances, and `UpdatedDate_DW >= @since` is UNKNOWN for exactly the new rows. That regression
    /// cost edw.Fara_Fact_Validation 106,143 rows before it was found.
    /// </summary>
    [Fact]
    public void Insert_StampsBothAuditColumns()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
        };
        var insert = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)
            .Single(s => s.StartsWith("INSERT INTO", StringComparison.Ordinal));

        Assert.Contains("[InsertedDate_DW]", insert, StringComparison.Ordinal);
        Assert.Contains("[UpdatedDate_DW]", insert, StringComparison.Ordinal);
        // One SYSUTCDATETIME() per stamped audit column, so both land with the same merge instant.
        Assert.Equal(2, insert.Split("SYSUTCDATETIME()").Length - 1);
    }

    [Fact]
    public void Insert_OmitsUpdatedDate_WhenTheFlowDoesNotDeclareIt()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            InsertedDateColumn = "InsertedDate_DW",
        };
        var insert = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)
            .Single(s => s.StartsWith("INSERT INTO", StringComparison.Ordinal));

        Assert.DoesNotContain("[UpdatedDate_DW]", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ProducesUpdateThenInsert()
    {
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"]));
        Assert.Equal(2, statements.Count);
        Assert.StartsWith("UPDATE", statements[0], StringComparison.Ordinal);
        Assert.Contains("HASHBYTES", statements[0], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO", statements[1], StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", statements[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SkipUpdate_OmitsUpdate()
    {
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"], skipUpdate: true));
        Assert.StartsWith("INSERT INTO", Assert.Single(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void SkipInsert_OmitsInsert()
    {
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"], skipInsert: true));
        Assert.StartsWith("UPDATE", Assert.Single(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void AllKeyColumns_ProduceOnlyInsert()
    {
        // No non-key columns means there is nothing to update; only the INSERT is emitted.
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id"], ["Id"]));
        Assert.StartsWith("INSERT INTO", Assert.Single(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void NoKeys_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id"], [])));

    [Fact]
    public void KeyNotInDataColumns_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Name"], ["Id"])));

    [Fact]
    public void UnknownHashAlgorithm_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"),
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], HashAlgorithm = "CRC32" }));

    // ---- Change detection compares the value the TARGET stores ----

    private static IReadOnlyDictionary<string, ChecksumColumnType> Types(params (string Column, string Staging, string Target)[] columns)
        => columns.ToDictionary(
            c => c.Column,
            c => new ChecksumColumnType { Staging = SqlDataType.Parse(c.Staging), Target = SqlDataType.Parse(c.Target) },
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DifferingType_IsConvertedOnTheStagingSideOnly()
    {
        // The staged nchar(36) renders '2828ca9b-...' and the stored uniqueidentifier '2828CA9B-...', so
        // without the conversion every matched row hashes as changed and is rewritten on every run.
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Uuid", "Name"],
            KeyColumns = ["Id"],
            ChecksumColumnTypes = Types(("Uuid", "nchar(36)", "uniqueidentifier"), ("Name", "nvarchar(50)", "nvarchar(50)")),
        };

        var update = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)[0];
        Assert.Contains("CONVERT(uniqueidentifier, src.[Uuid])", update, StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERT(uniqueidentifier, trg.[Uuid])", update, StringComparison.Ordinal);
        Assert.Contains("trg.[Uuid]", update, StringComparison.Ordinal);

        // A column whose two types already agree is hashed as it always was, on both sides.
        Assert.Contains("src.[Name]", update, StringComparison.Ordinal);
        Assert.Equal(1, update.Split("CONVERT(", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void UndeclaredColumns_AreHashedAsTheyAre()
    {
        var update = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"]))[0];
        Assert.DoesNotContain("CONVERT(", update, StringComparison.Ordinal);
    }

    [Theory]
    // CONCAT prints a datetime to the minute, so a second-level change would hash as no change at all.
    [InlineData("datetime", "datetime", "CONVERT(nvarchar(40), src.[V], 126)", "CONVERT(nvarchar(40), trg.[V], 126)")]
    // A differing type is converted first, then rendered by the target's style.
    [InlineData("datetime2(0)", "datetime", "CONVERT(nvarchar(40), CONVERT(datetime, src.[V]), 126)", "CONVERT(nvarchar(40), trg.[V], 126)")]
    // float defaults to six significant digits, money to two of its four decimals.
    [InlineData("float(53)", "float(53)", "CONVERT(nvarchar(40), src.[V], 3)", "CONVERT(nvarchar(40), trg.[V], 3)")]
    [InlineData("money", "money", "CONVERT(nvarchar(40), src.[V], 2)", "CONVERT(nvarchar(40), trg.[V], 2)")]
    // An exactly-rendered family keeps the bare column reference on both sides.
    [InlineData("int", "int", "src.[V]", "trg.[V]")]
    [InlineData("decimal(18, 2)", "decimal(18, 2)", "src.[V]", "trg.[V]")]
    public void LossyTypes_AreRenderedWithALosslessStyle(string staging, string target, string expectedSource, string expectedTarget)
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "V"],
            KeyColumns = ["Id"],
            ChecksumColumnTypes = Types(("V", staging, target)),
        };

        var update = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)[0];
        Assert.Contains(expectedSource, update, StringComparison.Ordinal);
        Assert.Contains(expectedTarget, update, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnTypes_ApplyToTheBatchedApply()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Stamp"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 500,
            ChecksumColumnTypes = Types(("Stamp", "datetime2(0)", "datetime")),
        };

        var update = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)[0];
        Assert.Contains("CONVERT(nvarchar(40), CONVERT(datetime, src.[Stamp]), 126)", update, StringComparison.Ordinal);
        Assert.Contains("CONVERT(nvarchar(40), trg.[Stamp], 126)", update, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnTypes_ApplyToTheDataSetLoop()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Stamp", "Ds"],
            KeyColumns = ["Id"],
            DataSetColumn = "Ds",
            ChecksumColumnTypes = Types(("Stamp", "datetime2(7)", "datetime2(3)")),
        };

        var script = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)[0];
        Assert.Contains("CONVERT(nvarchar(40), CONVERT(datetime2(3), src.[Stamp]), 126)", script, StringComparison.Ordinal);
        Assert.Contains("CONVERT(nvarchar(40), trg.[Stamp], 126)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnTypes_ApplyToTheScd2Close()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Amount"],
            KeyColumns = ["Id"],
            Scd2Enabled = true,
            Scd2ValidFromColumn = "ValidFrom_DW",
            Scd2ValidToColumn = "ValidTo_DW",
            Scd2CurrentFlagColumn = "IsCurrent_DW",
            Scd2AsOfLiteral = "2026-06-16 11:22:33.444",
            ChecksumColumnTypes = Types(("Amount", "decimal(18, 4)", "decimal(18, 2)")),
        };

        var close = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options)[1];
        Assert.Contains("CONVERT(decimal(18, 2), src.[Amount])", close, StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERT(decimal(18, 2), trg.[Amount])", close, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemDateColumns_AreStamped()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
        };
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options);
        Assert.Contains("[UpdatedDate_DW] = SYSUTCDATETIME()", statements[0], StringComparison.Ordinal);
        Assert.Contains("[InsertedDate_DW]", statements[1], StringComparison.Ordinal);
    }
}
