using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class MatchKeyGeneratorTests
{
    private static RelationalObject Target => new() { Database = "DW", Schema = "raw", Name = "Orders" };
    private static RelationalObject KeyTable => new() { Database = "DW", Schema = "raw", Name = "mkey_7_20240101_ab12cd34" };

    private static MatchKeyScriptOptions DefaultTagOptions(string[] keys) => new()
    {
        KeyColumns = keys,
        Action = MatchKeyAction.Tag,
        ActionThresholdPercent = 20,
        DeletedDateColumn = "DeletedDate_DW",
    };

    private static MatchKeyScriptOptions DeleteOptions(string[] keys) => new()
    {
        KeyColumns = keys,
        Action = MatchKeyAction.Delete,
        ActionThresholdPercent = 20,
    };

    [Fact]
    public void Generate_TagMode_ContainsTagUpdate_And_ResurrectUpdate()
    {
        var sql = MatchKeyGenerator.Generate(Target, KeyTable, DefaultTagOptions(["OrderId"]));

        Assert.Contains("UPDATE trg SET trg.[DeletedDate_DW] = SYSUTCDATETIME()", sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE trg SET trg.[DeletedDate_DW] = NULL", sql, StringComparison.Ordinal);
        Assert.Contains("@Resurrected = @@ROWCOUNT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE trg FROM", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DeleteMode_ContainsHardDelete_And_NoResurrect()
    {
        var sql = MatchKeyGenerator.Generate(Target, KeyTable, DeleteOptions(["OrderId"]));

        Assert.Contains("DELETE trg FROM", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@Resurrected = @@ROWCOUNT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DeletedDate_DW", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_NullSafeKeyEquality_InSql()
    {
        var sql = MatchKeyGenerator.Generate(Target, KeyTable, DefaultTagOptions(["OrderId", "Region"]));

        Assert.Contains("src.[OrderId] = trg.[OrderId] OR (src.[OrderId] IS NULL AND trg.[OrderId] IS NULL)", sql, StringComparison.Ordinal);
        Assert.Contains("src.[Region] = trg.[Region] OR (src.[Region] IS NULL AND trg.[Region] IS NULL)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ThresholdGuard_InSql()
    {
        var sql = MatchKeyGenerator.Generate(Target, KeyTable, DefaultTagOptions(["OrderId"]));

        Assert.Contains("@Candidates * 100.0 / @Total > 20", sql, StringComparison.Ordinal);
        Assert.Contains("SET @Breached = 1", sql, StringComparison.Ordinal);
        Assert.Contains("IF @Breached = 0 AND @Candidates > 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RowStatusColumn_StampedOnTag_ResetOnResurrect()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Tag,
            ActionThresholdPercent = 20,
            DeletedDateColumn = "DeletedDate_DW",
            RowStatusColumn = "RowStatus_DW",
        };

        var sql = MatchKeyGenerator.Generate(Target, KeyTable, options);

        Assert.Contains("trg.[RowStatus_DW] = 'D'", sql, StringComparison.Ordinal);
        Assert.Contains("trg.[RowStatus_DW] = 'U'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_IgnoreWindow_AddsDatePredicate_CandidateOnly()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Tag,
            ActionThresholdPercent = 20,
            DeletedDateColumn = "DeletedDate_DW",
            IgnoreDeletedRowsAfterMonths = 6,
            DateColumn = "OrderDate",
        };

        var sql = MatchKeyGenerator.Generate(Target, KeyTable, options);

        Assert.Contains("DATEADD(MONTH, -6", sql, StringComparison.Ordinal);
        Assert.Contains("[OrderDate]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TargetFilter_AppendedToPredicates()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Tag,
            ActionThresholdPercent = 20,
            DeletedDateColumn = "DeletedDate_DW",
            TargetFilter = "AND Region = 'NA'",
        };

        var sql = MatchKeyGenerator.Generate(Target, KeyTable, options);

        var count = System.Text.RegularExpressions.Regex.Count(sql, "AND Region = 'NA'");
        Assert.True(count >= 2, $"TargetFilter should appear in candidate count and in action statement; found {count}.");
    }

    [Fact]
    public void Generate_NoKeyColumns_Throws()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = [],
            Action = MatchKeyAction.Tag,
            DeletedDateColumn = "DeletedDate_DW",
        };

        Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(Target, KeyTable, options));
    }

    [Fact]
    public void Generate_TagModeWithoutDeletedDateColumn_Throws()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Tag,
            DeletedDateColumn = null,
        };

        var ex = Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(Target, KeyTable, options));
        Assert.Contains("DeletedDate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_IgnoreWindowWithoutDateColumn_Throws()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Tag,
            DeletedDateColumn = "DeletedDate_DW",
            IgnoreDeletedRowsAfterMonths = 3,
            DateColumn = null,
        };

        var ex = Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(Target, KeyTable, options));
        Assert.Contains("date column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_ThresholdOutOfRange_Throws()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Delete,
            ActionThresholdPercent = 101,
        };

        Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(Target, KeyTable, options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Generate_ThresholdBoundaryValues_Valid(int threshold)
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Delete,
            ActionThresholdPercent = threshold,
        };

        var sql = MatchKeyGenerator.Generate(Target, KeyTable, options);
        Assert.Contains($"100.0 / @Total > {threshold}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ThresholdNegative_Throws()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Delete,
            ActionThresholdPercent = -1,
        };

        Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(Target, KeyTable, options));
    }

    [Fact]
    public void Generate_ThresholdIsStrictlyGreaterThan_NotGte()
    {
        // The guard is ">" not ">=": exactly N% affected is NOT a breach.
        // An operator reading this SQL should know equal-to-threshold is allowed through.
        var sql = MatchKeyGenerator.Generate(Target, KeyTable, DefaultTagOptions(["OrderId"]));
        Assert.Contains($"@Candidates * 100.0 / @Total > {20}", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"@Candidates * 100.0 / @Total >= {20}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TargetFilter_AppearsInResurrectClause_TagMode()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Tag,
            ActionThresholdPercent = 20,
            DeletedDateColumn = "DeletedDate_DW",
            TargetFilter = "AND Region = 'NA'",
        };

        var sql = MatchKeyGenerator.Generate(Target, KeyTable, options);

        // TargetFilter must appear in: candidate SELECT, tag UPDATE, and resurrect UPDATE.
        var count = System.Text.RegularExpressions.Regex.Count(sql, "AND Region = 'NA'");
        Assert.True(count >= 3, $"TargetFilter should appear in candidate SELECT, tag UPDATE, and resurrect UPDATE; found {count}.");
    }

    [Fact]
    public void Generate_DeleteMode_TargetFilter_AppendedToDelete()
    {
        var options = new MatchKeyScriptOptions
        {
            KeyColumns = ["OrderId"],
            Action = MatchKeyAction.Delete,
            ActionThresholdPercent = 20,
            TargetFilter = "AND Region = 'EU'",
        };

        var sql = MatchKeyGenerator.Generate(Target, KeyTable, options);

        Assert.Contains("DELETE trg FROM", sql, StringComparison.Ordinal);
        Assert.Contains("AND Region = 'EU'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ClosingSelectRow_HasAllCounterAliases()
    {
        var sql = MatchKeyGenerator.Generate(Target, KeyTable, DefaultTagOptions(["OrderId"]));

        Assert.Contains("TotalRows", sql, StringComparison.Ordinal);
        Assert.Contains("CandidateRows", sql, StringComparison.Ordinal);
        Assert.Contains("AffectedRows", sql, StringComparison.Ordinal);
        Assert.Contains("ResurrectedRows", sql, StringComparison.Ordinal);
        Assert.Contains("ThresholdBreached", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_SquareBracketInIdentifier_IsEscaped()
    {
        // The generator qualifies objects as [schema].[name] (2-part, SQL runs in the target DB context).
        // Bracket escaping is validated on both the table name and the key column name.
        var target = new RelationalObject { Database = "db", Schema = "ra]w", Name = "Or]ders" };
        var sql = MatchKeyGenerator.Generate(target, KeyTable, DefaultTagOptions(["Or]erId"]));

        Assert.Contains("[ra]]w]", sql, StringComparison.Ordinal);
        Assert.Contains("[Or]]ders]", sql, StringComparison.Ordinal);
        Assert.Contains("[Or]]erId]", sql, StringComparison.Ordinal);
    }
}
