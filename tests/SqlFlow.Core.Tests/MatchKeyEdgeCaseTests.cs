using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge-case hardening for the key-match (deleted-row detection) feature: the SQL generator
/// (<see cref="MatchKeyGenerator"/>) and the YAML matchKeys mapping (<see cref="YamlIngestionFlowLoader"/>).
/// Every test is pure in-memory and deterministic (fixed inputs, no clock, no database). Cases here are
/// deliberately distinct from MatchKeyGeneratorTests, YamlIngestionMatchKeysTests, and the integration suite:
/// they probe raw-append whitespace handling, predicate ordering, identifier escaping on the soft-delete and
/// date columns, the unvalidated month window, action-vs-column interactions in Delete mode, and the
/// argument-null and key-arity boundaries.
/// </summary>
public sealed class MatchKeyEdgeCaseTests
{
    private static RelationalObject MkTarget() => new() { Database = "DW", Schema = "raw", Name = "Orders" };

    private static RelationalObject MkKeyTable() => new() { Database = "DW", Schema = "raw", Name = "mkey_7_20240101_ab12cd34" };

    private static MatchKeyScriptOptions MkTag(string[] keys) => new()
    {
        KeyColumns = keys,
        Action = MatchKeyAction.Tag,
        ActionThresholdPercent = 20,
        DeletedDateColumn = "DeletedDate_DW",
    };

    private static MatchKeyScriptOptions MkDelete(string[] keys) => new()
    {
        KeyColumns = keys,
        Action = MatchKeyAction.Delete,
        ActionThresholdPercent = 20,
    };

    private static readonly YamlIngestionFlowLoader MkLoader = new();

    private static string MkYaml(string matchKeysBlock) => $$"""
        flowType: ing
        name: orders
        connections:
          src: ${env:SRC}
          dwh: ${env:DWH}
        source:
          server: src
          object: db.dbo.Orders
        target:
          server: dwh
          object: dw.raw.Orders
        load:
          keyColumns: [OrderId]
          matchKeysInSourceAndTarget: true
        {{matchKeysBlock}}
        """;

    // ---- Generator: argument guards -------------------------------------------------------------------

    [Fact]
    public void Generate_NullTarget_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => MatchKeyGenerator.Generate(null!, MkKeyTable(), MkTag(["OrderId"])));
    }

    [Fact]
    public void Generate_NullKeyTable_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => MatchKeyGenerator.Generate(MkTarget(), null!, MkTag(["OrderId"])));
    }

    [Fact]
    public void Generate_NullOptions_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), null!));
    }

    // ---- Generator: composite-key null-safe equality arity --------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Generate_NullSafeEquality_HasOnePerKey(int keyCount)
    {
        var keys = Enumerable.Range(0, keyCount)
            .Select(i => "K" + i.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkTag(keys));

        // One NULL-safe OR-pair per key, re-emitted in three places in tag mode: the candidate-count anti-join,
        // the tag UPDATE anti-join (both reuse the same NOT EXISTS), and the resurrect EXISTS.
        foreach (var k in keys)
        {
            var pair = $"(src.[{k}] = trg.[{k}] OR (src.[{k}] IS NULL AND trg.[{k}] IS NULL))";
            Assert.Equal(3, CountOccurrences(sql, pair));
        }
    }

    [Fact]
    public void Generate_CompositeKeys_AreJoinedWithAnd_InDeclaredOrder()
    {
        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkTag(["A", "B", "C"]));

        var conjunction =
            "(src.[A] = trg.[A] OR (src.[A] IS NULL AND trg.[A] IS NULL)) AND " +
            "(src.[B] = trg.[B] OR (src.[B] IS NULL AND trg.[B] IS NULL)) AND " +
            "(src.[C] = trg.[C] OR (src.[C] IS NULL AND trg.[C] IS NULL))";
        Assert.Contains(conjunction, sql, StringComparison.Ordinal);
    }

    // ---- Generator: target filter raw-append whitespace handling --------------------------------------

    [Fact]
    public void Generate_TargetFilter_SurroundingWhitespaceTrimmedToSingleLeadingSpace()
    {
        var options = MkTag(["OrderId"]) with { TargetFilter = "   AND Region = 'NA'   " };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // The raw-append contract trims then prefixes exactly one space, so the predicate never glues to the
        // preceding token and never carries the user's stray padding.
        Assert.Contains(" AND Region = 'NA';", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("  AND Region = 'NA'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AND Region = 'NA'   ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TargetFilter_WhitespaceOnly_AppendsNothing()
    {
        var options = MkTag(["OrderId"]) with { TargetFilter = "   " };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // A blank filter is treated as absent: the candidate predicate ends right after the IS NULL guard.
        Assert.Contains("trg.[DeletedDate_DW] IS NULL;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NULL    ;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TargetFilter_NeverGluesToPrecedingPredicate()
    {
        var options = MkDelete(["OrderId"]) with { TargetFilter = "AND [Region] = N'x'" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // The DELETE WHERE must read "...) AND [Region]..." with a separating space, not ")AND".
        Assert.Contains(") AND [Region] = N'x';", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(")AND", sql, StringComparison.Ordinal);
    }

    // ---- Generator: ignore-window month boundaries (unvalidated) --------------------------------------

    [Fact]
    public void Generate_IgnoreWindow_ZeroMonths_EmitsDateAddMinusZero()
    {
        var options = MkTag(["OrderId"]) with { IgnoreDeletedRowsAfterMonths = 0, DateColumn = "OrderDate" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // Zero is accepted (no range guard) and renders verbatim as a -0 offset.
        Assert.Contains("DATEADD(MONTH, -0, SYSUTCDATETIME())", sql, StringComparison.Ordinal);
        Assert.Contains("trg.[OrderDate] >= DATEADD(MONTH, -0, SYSUTCDATETIME())", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_IgnoreWindow_NegativeMonths_EmitsDoubleNegativeOffset()
    {
        // No month-range validation exists (unlike the threshold guard), so a negative value flows through and
        // renders a double-negative offset. Pinned here to document the exact current output; flagged as a
        // suspected bug because a negative window is almost certainly a misconfiguration that should be rejected.
        var options = MkTag(["OrderId"]) with { IgnoreDeletedRowsAfterMonths = -3, DateColumn = "OrderDate" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.Contains("DATEADD(MONTH, --3, SYSUTCDATETIME())", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_IgnoreWindow_AppearsInCandidateCountAndTagUpdate_NotInResurrect()
    {
        var options = MkTag(["OrderId"]) with { IgnoreDeletedRowsAfterMonths = 6, DateColumn = "OrderDate" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // The window bounds only the candidate side (count + tag UPDATE), twice. Un-tagging is always safe, so
        // the resurrect UPDATE must not be date-bounded.
        Assert.Equal(2, CountOccurrences(sql, "DATEADD(MONTH, -6, SYSUTCDATETIME())"));
        var resurrectStart = sql.IndexOf("UPDATE trg SET trg.[DeletedDate_DW] = NULL", StringComparison.Ordinal);
        Assert.True(resurrectStart > 0, "Resurrect UPDATE should be present in tag mode.");
        Assert.DoesNotContain("DATEADD", sql[resurrectStart..], StringComparison.Ordinal);
    }

    // ---- Generator: Delete-mode interactions with tag-only options ------------------------------------

    [Fact]
    public void Generate_DeleteMode_IgnoresIgnoreWindow_NoDateAdd_AndDoesNotThrow()
    {
        // The window is a Tag-mode concept. In Delete mode the generator must not emit it, yet it still
        // validates the date-column-present rule (so this does not throw despite the window being set).
        var options = MkDelete(["OrderId"]) with { IgnoreDeletedRowsAfterMonths = 6, DateColumn = "OrderDate" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.Contains("DELETE trg FROM", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DATEADD", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[OrderDate]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DeleteMode_IgnoresRowStatusColumn()
    {
        var options = MkDelete(["OrderId"]) with { RowStatusColumn = "RowStatus_DW" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.DoesNotContain("RowStatus_DW", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("= 'D'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("= 'U'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DeleteMode_IgnoresDeletedDateColumn_StillHardDeletes()
    {
        // A stray DeletedDate column set on a Delete policy must not turn the hard delete into a soft tag.
        var options = MkDelete(["OrderId"]) with { DeletedDateColumn = "DeletedDate_DW" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.Contains("DELETE trg FROM", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DeletedDate_DW", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSUTCDATETIME()", sql[..sql.IndexOf("SELECT @Total AS TotalRows", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    // ---- Generator: identifier escaping on non-key columns and the key table --------------------------

    [Fact]
    public void Generate_DeletedDateColumn_BracketEscaped_InTagAndResurrect()
    {
        var options = MkTag(["OrderId"]) with { DeletedDateColumn = "Del]Date" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.Contains("trg.[Del]]Date] = SYSUTCDATETIME()", sql, StringComparison.Ordinal);
        Assert.Contains("trg.[Del]]Date] = NULL", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE trg.[Del]]Date] IS NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RowStatusColumn_BracketEscaped()
    {
        var options = MkTag(["OrderId"]) with { RowStatusColumn = "RS]C" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.Contains("trg.[RS]]C] = 'D'", sql, StringComparison.Ordinal);
        Assert.Contains("trg.[RS]]C] = 'U'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DateColumn_BracketEscaped_InWindow()
    {
        var options = MkTag(["OrderId"]) with { IgnoreDeletedRowsAfterMonths = 6, DateColumn = "Ord]Date" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        Assert.Contains("trg.[Ord]]Date] >= DATEADD(MONTH, -6, SYSUTCDATETIME())", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_KeyTableName_BracketEscaped_InAntiJoin()
    {
        var keyTable = new RelationalObject { Database = "DW", Schema = "ra]w", Name = "mk]ey" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), keyTable, MkTag(["OrderId"]));

        Assert.Contains("FROM [ra]]w].[mk]]ey] AS src", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_KeyTable_QualifiedTwoPart_DatabaseOmitted()
    {
        // The key table is referenced as [schema].[name]; the script runs in the target DB context so the
        // database part is intentionally not emitted.
        var keyTable = new RelationalObject { Database = "OtherDb", Schema = "stg", Name = "mkey_1" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), keyTable, MkTag(["OrderId"]));

        Assert.Contains("[stg].[mkey_1]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OtherDb", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[OtherDb].[stg].[mkey_1]", sql, StringComparison.Ordinal);
    }

    // ---- Generator: structural / safety invariants ----------------------------------------------------

    [Fact]
    public void Generate_ThresholdGuard_HasTotalGreaterThanZeroPrefix_AvoidsDivideByZero()
    {
        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkTag(["OrderId"]));

        // The ratio is only evaluated when @Total > 0, so an empty target never divides by zero and never
        // reports a breach.
        Assert.Contains("IF @Total > 0 AND @Candidates * 100.0 / @Total >", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(99)]
    public void Generate_ThresholdLiteral_RendersExactValue(int threshold)
    {
        var options = MkDelete(["OrderId"]) with { ActionThresholdPercent = threshold };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        var literal = threshold.ToString(CultureInfo.InvariantCulture);
        Assert.Contains($"@Candidates * 100.0 / @Total > {literal}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TagMode_CandidatePredicate_OnlyTouchesUntaggedRows()
    {
        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkTag(["OrderId"]));

        // The "IS NULL" guard keeps the count and the tag UPDATE from re-tagging already-tagged rows; it
        // appears exactly twice (candidate count + tag UPDATE) and never in the resurrect branch.
        Assert.Equal(2, CountOccurrences(sql, "AND trg.[DeletedDate_DW] IS NULL"));
    }

    [Fact]
    public void Generate_TagMode_WithoutRowStatus_TagStampHasNoSecondaryAssignment()
    {
        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkTag(["OrderId"]));

        // With no row-status column the SET clause is a single assignment: there must be no comma stamp.
        Assert.Contains("UPDATE trg SET trg.[DeletedDate_DW] = SYSUTCDATETIME() FROM", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSUTCDATETIME(), trg.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Resurrect_UsesPositiveExists_AndIsNotNull()
    {
        var options = MkTag(["OrderId"]) with { RowStatusColumn = "RowStatus_DW" };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // Resurrect is the mirror of the anti-join: a previously tagged row (IS NOT NULL) whose key is back
        // (EXISTS, not NOT EXISTS). Both stamps reset.
        Assert.Contains("WHERE trg.[DeletedDate_DW] IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("AND EXISTS (SELECT 1 FROM", sql, StringComparison.Ordinal);
        Assert.Contains("trg.[DeletedDate_DW] = NULL, trg.[RowStatus_DW] = 'U'", sql, StringComparison.Ordinal);
        Assert.Contains("SET @Resurrected = @@ROWCOUNT;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DeleteMode_ActionStatement_HasNoCandidatePredicates()
    {
        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkDelete(["OrderId"]));

        // The DELETE matches purely on the anti-join (the NOT EXISTS), with no IS NULL / window predicate.
        Assert.Contains("DELETE trg FROM [raw].[Orders] AS trg WHERE NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NULL AND trg.[DeletedDate_DW]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ScriptShape_StartsWithNoCount_EndsWithCountersSelect()
    {
        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), MkTag(["OrderId"]));

        Assert.StartsWith("SET NOCOUNT ON;", sql, StringComparison.Ordinal);
        var trimmed = sql.TrimEnd();
        Assert.EndsWith(
            "SELECT @Total AS TotalRows, @Candidates AS CandidateRows, @Affected AS AffectedRows, @Resurrected AS ResurrectedRows, @Breached AS ThresholdBreached;",
            trimmed,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TagMode_RowStatusAndWindowCombined_BothEmitted()
    {
        var options = MkTag(["OrderId"]) with
        {
            RowStatusColumn = "RowStatus_DW",
            IgnoreDeletedRowsAfterMonths = 12,
            DateColumn = "OrderDate",
        };

        var sql = MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options);

        // The status stamp and the date window are independent features and must coexist in the tag UPDATE.
        Assert.Contains("trg.[DeletedDate_DW] = SYSUTCDATETIME(), trg.[RowStatus_DW] = 'D'", sql, StringComparison.Ordinal);
        Assert.Contains("DATEADD(MONTH, -12, SYSUTCDATETIME())", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TagWithEmptyDeletedDateColumn_Throws()
    {
        // An empty (not null) string is still "no column": the whitespace check must reject it.
        var options = MkTag(["OrderId"]) with { DeletedDateColumn = "" };

        Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options));
    }

    [Fact]
    public void Generate_TagWithWhitespaceDeletedDateColumn_Throws()
    {
        var options = MkTag(["OrderId"]) with { DeletedDateColumn = "   " };

        Assert.Throws<SqlFlowException>(() => MatchKeyGenerator.Generate(MkTarget(), MkKeyTable(), options));
    }

    // ---- YAML mapping: key-column override and inheritance --------------------------------------------

    [Fact]
    public void Yaml_MatchKeysKeyColumns_OverrideMapsThrough()
    {
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: tag
              keyColumns: [CustomerId, Region]
            """));

        Assert.Equal(["CustomerId", "Region"], doc.Flow.MatchKeys.KeyColumns);
    }

    [Fact]
    public void Yaml_MatchKeysWithoutKeyColumns_LeavesOverrideEmpty()
    {
        // Empty means "inherit load.keyColumns at run time"; the policy itself must stay empty, not be
        // pre-filled with the load keys here.
        var doc = MkLoader.Parse(MkYaml("matchKeys:\n  action: tag"));

        Assert.Empty(doc.Flow.MatchKeys.KeyColumns);
    }

    // ---- YAML mapping: raw-append filters and blank normalization -------------------------------------

    [Fact]
    public void Yaml_SourceAndTargetFilters_RoundTripVerbatim()
    {
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: tag
              sourceFilter: "AND [Status] = N'A'"
              targetFilter: "AND [Region] IN (N'NA', N'EU')"
            """));

        var mk = doc.Flow.MatchKeys;
        Assert.Equal("AND [Status] = N'A'", mk.SourceFilter);
        Assert.Equal("AND [Region] IN (N'NA', N'EU')", mk.TargetFilter);
    }

    [Theory]
    [InlineData("sourceFilter")]
    [InlineData("targetFilter")]
    public void Yaml_BlankFilter_MapsToNull(string field)
    {
        // A quoted run of spaces is normalized to null so it does not become a no-op " " appended predicate.
        var doc = MkLoader.Parse(MkYaml($"matchKeys:\n  {field}: \"   \""));

        var mk = doc.Flow.MatchKeys;
        if (field == "sourceFilter")
        {
            Assert.Null(mk.SourceFilter);
        }
        else
        {
            Assert.Null(mk.TargetFilter);
        }
    }

    // ---- YAML mapping: date column and month window ---------------------------------------------------

    [Fact]
    public void Yaml_DateColumnWithoutIgnoreWindow_Maps_AndMonthsStaysNull()
    {
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: tag
              dateColumn: OrderDate
            """));

        var mk = doc.Flow.MatchKeys;
        Assert.Equal("OrderDate", mk.DateColumn);
        Assert.Null(mk.IgnoreDeletedRowsAfterMonths);
    }

    [Fact]
    public void Yaml_IgnoreWindowZeroMonths_WithDateColumn_MapsZero()
    {
        // Zero is "not null", so it requires a date column; with one present it maps verbatim (no range guard).
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: tag
              ignoreDeletedRowsAfterMonths: 0
              dateColumn: OrderDate
            """));

        Assert.Equal(0, doc.Flow.MatchKeys.IgnoreDeletedRowsAfterMonths);
    }

    [Fact]
    public void Yaml_IgnoreWindowNegativeMonths_WithDateColumn_MapsNegative()
    {
        // The YAML mapper validates the date-column dependency but, unlike thresholdPercent, applies no range
        // guard to the month count. Pinned to the current behavior; flagged as a suspected bug.
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: tag
              ignoreDeletedRowsAfterMonths: -2
              dateColumn: OrderDate
            """));

        Assert.Equal(-2, doc.Flow.MatchKeys.IgnoreDeletedRowsAfterMonths);
    }

    // ---- YAML mapping: action parsing edges -----------------------------------------------------------

    [Theory]
    [InlineData("  tag  ", MatchKeyAction.Tag)]
    [InlineData("\ttag", MatchKeyAction.Tag)]
    [InlineData("  delete  ", MatchKeyAction.Delete)]
    public void Yaml_Action_SurroundingWhitespaceTrimmed(string spelling, MatchKeyAction expected)
    {
        // YamlDotNet would keep a quoted scalar's spaces; the mapper trims before matching.
        var doc = MkLoader.Parse(MkYaml($"matchKeys:\n  action: \"{spelling}\""));

        Assert.Equal(expected, doc.Flow.MatchKeys.Action);
    }

    [Theory]
    [InlineData("tAg", MatchKeyAction.Tag)]
    [InlineData("dElEtE", MatchKeyAction.Delete)]
    public void Yaml_Action_MixedCase_Parses(string spelling, MatchKeyAction expected)
    {
        var doc = MkLoader.Parse(MkYaml($"matchKeys:\n  action: {spelling}"));

        Assert.Equal(expected, doc.Flow.MatchKeys.Action);
    }

    [Fact]
    public void Yaml_InvalidAction_MessageNamesTokenAndAllowedValues()
    {
        var ex = Assert.Throws<FlowValidationException>(
            () => MkLoader.Parse(MkYaml("matchKeys:\n  action: archive")));

        Assert.Contains("archive", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tag", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- YAML mapping: DeletedDate auto-enable interactions -------------------------------------------

    [Fact]
    public void Yaml_DeleteMode_ExplicitDeletedDateTrue_IsPreserved()
    {
        // Auto-enable only adds the column for Tag mode; it must never strip an explicitly requested
        // DeletedDate column in Delete mode.
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: delete
            systemColumns:
              deletedDate: true
            """));

        Assert.Equal(MatchKeyAction.Delete, doc.Flow.MatchKeys.Action);
        Assert.True(doc.Flow.SystemColumns.DeletedDate);
    }

    [Fact]
    public void Yaml_TagAction_ButMatchKeysDisabled_DoesNotAutoEnableDeletedDate()
    {
        // Auto-enable requires BOTH load.matchKeysInSourceAndTarget AND a Tag action. With matching turned off,
        // a Tag action alone must not flip the soft-delete column on.
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.dbo.Orders
            target:
              server: dwh
              object: dw.raw.Orders
            load:
              keyColumns: [OrderId]
              matchKeysInSourceAndTarget: false
            matchKeys:
              action: tag
            """;

        var doc = MkLoader.Parse(yaml);
        Assert.False(doc.Flow.Load.MatchKeysInSourceAndTarget);
        Assert.False(doc.Flow.SystemColumns.DeletedDate);
    }

    [Fact]
    public void Yaml_DeleteMode_ThresholdAndFiltersStillMap()
    {
        // Delete mode does not auto-enable DeletedDate, but the rest of the matchKeys block must still map.
        var doc = MkLoader.Parse(MkYaml("""
            matchKeys:
              action: delete
              thresholdPercent: 75
              targetFilter: "AND [Region] = N'NA'"
            """));

        var mk = doc.Flow.MatchKeys;
        Assert.Equal(MatchKeyAction.Delete, mk.Action);
        Assert.Equal(75, mk.ActionThresholdPercent);
        Assert.Equal("AND [Region] = N'NA'", mk.TargetFilter);
        Assert.False(doc.Flow.SystemColumns.DeletedDate);
    }

    [Fact]
    public void Yaml_ThresholdPercent_JustOverMax_ThrowsWithValue()
    {
        var ex = Assert.Throws<FlowValidationException>(
            () => MkLoader.Parse(MkYaml("matchKeys:\n  thresholdPercent: 101")));

        Assert.Contains("101", ex.Message, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
        => Regex.Count(haystack, Regex.Escape(needle));
}
