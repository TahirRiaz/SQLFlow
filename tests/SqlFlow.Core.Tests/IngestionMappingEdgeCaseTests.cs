using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Ingestion.Legacy;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Edge-case hardening for the legacy ingestion mapping surface (IngestionFlowMapper.FromLegacy, the
/// comma-list parsing it delegates to, and the three-part object parsing), plus the few ingestion-mapping
/// concerns the schema builder owns (source-to-target name map under cleanup). Every case here is a boundary
/// the baseline IngestionFlowMapperTests / IngestionSchemaBuilderTests do not already assert. Pure and
/// in-memory: no database, no clock, no randomness.
/// </summary>
public sealed class IngestionMappingEdgeCaseTests
{
    // A distinctly named local builder so it cannot collide with helpers in sibling files in this namespace.
    private static LegacyIngestionRow EdgeRow() => new()
    {
        FlowID = 7,
        srcServer = "src",
        srcDBSchTbl = "[Db].[dbo].[Src]",
        trgServer = "trg",
        trgDBSchTbl = "[Db].[stg].[Trg]",
    };

    private static IngestionFlow MapEdge(Action<LegacyIngestionRow> configure)
    {
        var row = EdgeRow();
        configure(row);
        return IngestionFlowMapper.FromLegacy(row);
    }

    // ---------------------------------------------------------------------------------------------------
    // Comma-separated list parsing (KeyColumns is the representative field; the same parser feeds the rest).
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(",A,B", new[] { "A", "B" })]                  // leading comma yields an empty token, dropped
    [InlineData("A,B,", new[] { "A", "B" })]                  // trailing comma yields an empty token, dropped
    [InlineData("A,,B", new[] { "A", "B" })]                  // interior empty token dropped
    [InlineData("A, ,B", new[] { "A", "B" })]                 // whitespace-only token dropped
    [InlineData("  ,  ,  ", new string[0])]                   // all-empty list resolves to nothing
    [InlineData("A,A,a", new[] { "A", "A", "a" })]            // duplicates are preserved verbatim (no dedup)
    [InlineData("  A  ", new[] { "A" })]                      // surrounding whitespace trimmed
    [InlineData("A\tB", new[] { "A\tB" })]                    // a tab is not a delimiter, only the comma is
    public void KeyColumns_CommaParsing_DropsEmptyAndTrims(string input, string[] expected)
    {
        var flow = MapEdge(r => r.KeyColumns = input);
        Assert.Equal(expected, flow.Load.KeyColumns);
    }

    [Fact]
    public void KeyColumns_Semicolon_IsNotADelimiter_StaysOneToken()
    {
        // The legacy list parser splits on commas only. A semicolon-separated string is therefore a single
        // identifier (this guards against a future "also split on ';'" change silently breaking ports).
        var flow = MapEdge(r => r.KeyColumns = "Region;Country");
        Assert.Equal(new[] { "Region;Country" }, flow.Load.KeyColumns);
    }

    [Fact]
    public void KeyColumns_BracketedItem_ContainingComma_IsNotSplit()
    {
        // A comma inside a [bracketed] identifier must not split the list.
        var flow = MapEdge(r => r.KeyColumns = "[Last, First],City");
        Assert.Equal(new[] { "Last, First", "City" }, flow.Load.KeyColumns);
    }

    [Fact]
    public void KeyColumns_BracketedItem_ContainingPeriod_KeepsThePeriod()
    {
        // A period inside brackets is part of the name (only the object parser splits on '.', and only
        // outside brackets); the list parser keeps it.
        var flow = MapEdge(r => r.KeyColumns = "[a.b],c");
        Assert.Equal(new[] { "a.b", "c" }, flow.Load.KeyColumns);
    }

    [Fact]
    public void KeyColumns_EscapedClosingBracket_IsUnescapedInItem()
    {
        // An inner ']' is escaped as ']]'; the parsed item collapses it back to a single ']'.
        var flow = MapEdge(r => r.KeyColumns = "[Weird]]Name],Plain");
        Assert.Equal(new[] { "Weird]Name", "Plain" }, flow.Load.KeyColumns);
    }

    [Fact]
    public void IgnoreColumns_UnterminatedBracket_SwallowsRestOfListIntoOneToken()
    {
        // An opening '[' with no matching ']' puts the parser in "inside bracket" mode for the remainder of
        // the string, so the comma is NOT treated as a separator and the whole tail collapses into a single
        // token. The mapping must not throw on this malformed input; it carries the swallowed token verbatim.
        var flow = MapEdge(r => r.IgnoreColumns = "[Open,Next");
        Assert.Equal(new[] { "[Open,Next" }, flow.Source.IgnoreColumns);
    }

    [Fact]
    public void HashKeyColumns_OnlyCommas_ResultsInNoHashKey()
    {
        // A string of only separators parses to an empty column set, so there is no hash key.
        var flow = MapEdge(r => r.HashKeyColumns = ",,,");
        Assert.False(flow.Change.HasHashKey);
        Assert.Empty(flow.Change.HashColumns);
    }

    [Fact]
    public void IncrementalColumns_PartialBracketSuffix_TrimsBracketsOnlyWhenBothSidesPresent()
    {
        // "Col]" has a trailing ']' but no leading '['; it is not a bracket-quoted name, so it stays literal.
        var flow = MapEdge(r => r.IncrementalColumns = "Col],Two");
        Assert.Equal(new[] { "Col]", "Two" }, flow.Incremental.Columns);
    }

    // ---------------------------------------------------------------------------------------------------
    // Default assertions list parsing.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Assertions_Null_AppliesTheTwoLegacyDefaults()
    {
        // A NULL Assertions column means "use the legacy default pair", distinct from the empty-string case
        // the baseline already covers.
        var flow = MapEdge(r => r.Assertions = null);
        Assert.Equal(new[] { "CheckEmptyTable", "CheckFreshnessDaily" }, flow.Assertions);
    }

    [Fact]
    public void Assertions_WhitespaceOnly_MeansNone_NotTheDefaults()
    {
        // Whitespace is not NULL, so the default substitution does not kick in; the parser then drops the
        // blank token, leaving an empty assertion set.
        var flow = MapEdge(r => r.Assertions = "   ");
        Assert.Empty(flow.Assertions);
    }

    [Fact]
    public void Assertions_CustomList_OverridesDefaults_AndKeepsOrder()
    {
        var flow = MapEdge(r => r.Assertions = "CheckDuplicateKey, [Check Row Count] ,CheckNulls");
        Assert.Equal(new[] { "CheckDuplicateKey", "Check Row Count", "CheckNulls" }, flow.Assertions);
    }

    [Fact]
    public void Assertions_DuplicateNames_ArePreserved()
    {
        // The mapper does not de-duplicate assertion names; a repeated name is carried through as written.
        var flow = MapEdge(r => r.Assertions = "CheckEmptyTable,CheckEmptyTable");
        Assert.Equal(new[] { "CheckEmptyTable", "CheckEmptyTable" }, flow.Assertions);
    }

    // ---------------------------------------------------------------------------------------------------
    // System column toggles (SysColumns).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SysColumns_Null_AppliesTheTwoLegacyDefaults()
    {
        var flow = MapEdge(r => r.SysColumns = null);
        Assert.True(flow.SystemColumns.InsertedDate);
        Assert.True(flow.SystemColumns.UpdatedDate);
        Assert.False(flow.SystemColumns.DeletedDate);
        Assert.False(flow.SystemColumns.RowStatus);
    }

    [Fact]
    public void SysColumns_WhitespaceOnly_MeansNone()
    {
        // Whitespace bypasses the NULL default and parses to no columns: every system column is off.
        var flow = MapEdge(r => r.SysColumns = "   ");
        Assert.False(flow.SystemColumns.InsertedDate);
        Assert.False(flow.SystemColumns.UpdatedDate);
        Assert.False(flow.SystemColumns.DeletedDate);
        Assert.False(flow.SystemColumns.RowStatus);
    }

    [Theory]
    [InlineData("inserteddate_dw")]
    [InlineData("INSERTEDDATE_DW")]
    [InlineData("InsertedDate_DW")]
    [InlineData("  InsertedDate_DW  ")]
    [InlineData("[InsertedDate_DW]")]
    public void SysColumns_TokenMatch_IsCaseInsensitive_TrimmedAndBracketTolerant(string token)
    {
        var flow = MapEdge(r => r.SysColumns = token);
        Assert.True(flow.SystemColumns.InsertedDate);
        Assert.False(flow.SystemColumns.UpdatedDate);
    }

    [Fact]
    public void SysColumns_AllFour_TurnsEveryColumnOn()
    {
        var flow = MapEdge(r => r.SysColumns = "RowStatus_DW,DeletedDate_DW,UpdatedDate_DW,InsertedDate_DW");
        Assert.True(flow.SystemColumns.InsertedDate);
        Assert.True(flow.SystemColumns.UpdatedDate);
        Assert.True(flow.SystemColumns.DeletedDate);
        Assert.True(flow.SystemColumns.RowStatus);
    }

    [Fact]
    public void SysColumns_RepeatedToken_IsIdempotent()
    {
        var flow = MapEdge(r => r.SysColumns = "DeletedDate_DW,DeletedDate_DW");
        Assert.True(flow.SystemColumns.DeletedDate);
        Assert.False(flow.SystemColumns.InsertedDate);
    }

    [Fact]
    public void SysColumns_UnknownToken_ErrorNamesTheFlowId()
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapEdge(r => r.SysColumns = "Created_DW"));
        Assert.Contains("Created_DW", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchKeysInSrcTrg_WithTagAction_ForcesDeletedDateColumnOn_EvenWhenSysColumnsEmpty()
    {
        // Tag-mode soft delete stamps DeletedDate_DW, so the column must exist even though the explicit
        // SysColumns list omitted it. Default match-key action is Tag, so passing a row (with no ActionType)
        // is enough.
        var row = EdgeRow();
        row.SysColumns = "";
        row.MatchKeysInSrcTrg = true;
        var flow = IngestionFlowMapper.FromLegacy(row, null, null, new LegacyMatchKeyRow { MatchKeyID = 1, FlowID = 7 });

        Assert.True(flow.SystemColumns.DeletedDate);
        Assert.False(flow.SystemColumns.InsertedDate);
        Assert.Equal(MatchKeyAction.Tag, flow.MatchKeys.Action);
    }

    [Fact]
    public void MatchKeysInSrcTrg_WithDeleteAction_DoesNotForceDeletedDateColumn()
    {
        // Hard delete removes the row, so no DeletedDate_DW is forced; the explicit (empty) SysColumns wins.
        var row = EdgeRow();
        row.SysColumns = "";
        row.MatchKeysInSrcTrg = true;
        var flow = IngestionFlowMapper.FromLegacy(
            row, null, null, new LegacyMatchKeyRow { MatchKeyID = 1, FlowID = 7, ActionType = "Delete" });

        Assert.False(flow.SystemColumns.DeletedDate);
        Assert.Equal(MatchKeyAction.Delete, flow.MatchKeys.Action);
    }

    [Fact]
    public void MatchKey_NullRow_LeavesDefaultPolicy_AndDoesNotForceDeletedDate()
    {
        // With no match-key row supplied, even MatchKeysInSrcTrg=true keeps the default Tag policy but the
        // DeletedDate force only triggers off the explicit (default) Tag action; here SysColumns is empty and
        // there is no match-key row, so nothing forces the column on beyond the default action path.
        var row = EdgeRow();
        row.SysColumns = "InsertedDate_DW";
        var flow = IngestionFlowMapper.FromLegacy(row, null, null, null);

        Assert.Equal(MatchKeyAction.Tag, flow.MatchKeys.Action);
        Assert.Equal(20, flow.MatchKeys.ActionThresholdPercent);
        Assert.False(flow.SystemColumns.DeletedDate);
    }

    [Theory]
    [InlineData(null, MatchKeyAction.Tag)]      // null ActionType defaults to Tag
    [InlineData("tag", MatchKeyAction.Tag)]
    [InlineData("TAG", MatchKeyAction.Tag)]
    [InlineData("  Delete  ", MatchKeyAction.Delete)]   // trimmed and case-folded
    [InlineData("delete", MatchKeyAction.Delete)]
    public void MatchKey_ActionType_ParsedCaseInsensitiveAndTrimmed(string? actionType, MatchKeyAction expected)
    {
        var row = EdgeRow();
        var flow = IngestionFlowMapper.FromLegacy(
            row, null, null, new LegacyMatchKeyRow { MatchKeyID = 2, FlowID = 7, ActionType = actionType });

        Assert.Equal(expected, flow.MatchKeys.Action);
    }

    [Fact]
    public void MatchKey_UnknownActionType_FailsFast_NamingTheValue()
    {
        var row = EdgeRow();
        var ex = Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(
            row, null, null, new LegacyMatchKeyRow { MatchKeyID = 2, FlowID = 7, ActionType = "wipe" }));
        Assert.Contains("wipe", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(null, 20)]   // omitted threshold falls back to the legacy default of 20
    public void MatchKey_ThresholdPercent_BoundaryAndDefault(int? configured, int expected)
    {
        var row = EdgeRow();
        var flow = IngestionFlowMapper.FromLegacy(
            row, null, null, new LegacyMatchKeyRow { MatchKeyID = 2, FlowID = 7, ActionThresholdPercent = configured });

        Assert.Equal(expected, flow.MatchKeys.ActionThresholdPercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void MatchKey_ThresholdPercent_OutOfRange_FailsFast(int outOfRange)
    {
        var row = EdgeRow();
        var ex = Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(
            row, null, null, new LegacyMatchKeyRow { MatchKeyID = 2, FlowID = 7, ActionThresholdPercent = outOfRange }));
        Assert.Contains(outOfRange.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchKey_KeyColumnsAndFilters_AreParsedAndCarried()
    {
        var row = EdgeRow();
        var flow = IngestionFlowMapper.FromLegacy(row, null, null, new LegacyMatchKeyRow
        {
            MatchKeyID = 2,
            FlowID = 7,
            KeyColumns = "[Order Id], Region",
            srcFilter = "AND Active = 1",
            trgFilter = "AND Region = 'NA'",
            DateColumn = "OrderDate",
        });

        Assert.Equal(new[] { "Order Id", "Region" }, flow.MatchKeys.KeyColumns);
        Assert.Equal("AND Active = 1", flow.MatchKeys.SourceFilter);
        Assert.Equal("AND Region = 'NA'", flow.MatchKeys.TargetFilter);
        Assert.Equal("OrderDate", flow.MatchKeys.DateColumn);
    }

    // ---------------------------------------------------------------------------------------------------
    // Three-part object parsing (srcDBSchTbl / trgDBSchTbl), exercised through the mapper's wrapping.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void FourPartSource_DropsLeadingServerPart()
    {
        var flow = MapEdge(r => r.srcDBSchTbl = "[srv].[Db].[dbo].[Customer]");
        Assert.Equal("Db", flow.Source.Table.Database);
        Assert.Equal("dbo", flow.Source.Table.Schema);
        Assert.Equal("Customer", flow.Source.Table.Name);
    }

    [Fact]
    public void Source_BracketedDottedParts_ArePreserved()
    {
        var flow = MapEdge(r => r.srcDBSchTbl = "[my.db].[my schema].[my.tbl]");
        Assert.Equal("my.db", flow.Source.Table.Database);
        Assert.Equal("my schema", flow.Source.Table.Schema);
        Assert.Equal("my.tbl", flow.Source.Table.Name);
    }

    [Fact]
    public void Source_UnbracketedWithSpaces_IsTrimmedPerPart()
    {
        var flow = MapEdge(r => r.srcDBSchTbl = " Db . dbo . Customer ");
        Assert.Equal("Db", flow.Source.Table.Database);
        Assert.Equal("dbo", flow.Source.Table.Schema);
        Assert.Equal("Customer", flow.Source.Table.Name);
    }

    [Theory]
    [InlineData("Db..Tbl")]      // empty schema part
    [InlineData(".dbo.Tbl")]     // empty database part
    [InlineData("Db.dbo.")]      // empty object part
    public void Source_EmptyMiddleOrEdgePart_FailsFast_NamingFlowAndColumn(string name)
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapEdge(r => r.srcDBSchTbl = name));
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("srcDBSchTbl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPartSource_FailsFast_WithFlowAndColumnContext()
    {
        // The baseline asserts the two-part TARGET failure; this asserts the SOURCE column is named too.
        var ex = Assert.Throws<SqlFlowException>(() => MapEdge(r => r.srcDBSchTbl = "dbo.Customer"));
        Assert.Contains("srcDBSchTbl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhitespaceOnlySource_IsTreatedAsMissingRequiredColumn()
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapEdge(r => r.srcDBSchTbl = "   "));
        Assert.Contains("srcDBSchTbl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTrgServer_FailsFast_NamingThatColumn()
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapEdge(r => r.trgServer = " "));
        Assert.Contains("trgServer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionReferences_AreServerAliasPrefixedWithAt()
    {
        var flow = MapEdge(r =>
        {
            r.srcServer = "Crm";
            r.trgServer = "Dwh";
        });
        Assert.Equal("@Crm", flow.Source.ConnectionReference);
        Assert.Equal("@Dwh", flow.Target.ConnectionReference);
    }

    // ---------------------------------------------------------------------------------------------------
    // Numeric / boolean default coalescing for omitted columns.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, null)]      // legacy 0 means "engine default"
    [InlineData(-1, null)]     // a negative thread count is not positive, so also engine default
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    public void Threads_OnlyPositiveValuesSurvive_OthersBecomeEngineDefault(int configured, int? expected)
    {
        var flow = MapEdge(r => r.NoOfThreads = configured);
        Assert.Equal(expected, flow.Load.Threads);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-5, true)]     // FullLoad is an int treated as truthy: any non-zero (even negative) is full
    [InlineData(int.MaxValue, true)]
    public void FullLoad_IsNonZeroTruthy(int configured, bool expected)
    {
        var flow = MapEdge(r => r.FullLoad = configured);
        Assert.Equal(expected, flow.Incremental.FullLoad);
    }

    [Fact]
    public void OmittedNumericColumns_FallBackToDocumentedDefaults()
    {
        var flow = IngestionFlowMapper.FromLegacy(EdgeRow());
        Assert.Equal(7, flow.Incremental.OverlapDays);
        Assert.Equal(2000, flow.Load.BatchUpsertRowCount);
        Assert.Null(flow.Load.Threads);
        Assert.Equal(20, flow.MatchKeys.ActionThresholdPercent);
    }

    [Fact]
    public void ExplicitZeroOverlapDays_IsHonored_NotReplacedByDefault()
    {
        // 0 is a real, distinct-from-NULL value; the null-coalescing default of 7 must not override it.
        var flow = MapEdge(r => r.NoOfOverlapDays = 0);
        Assert.Equal(0, flow.Incremental.OverlapDays);
    }

    [Fact]
    public void ExplicitZeroBatchUpsertRowCount_IsHonored()
    {
        var flow = MapEdge(r => r.BatchUpsertRowCount = 0);
        Assert.Equal(0, flow.Load.BatchUpsertRowCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StreamData_NullableBool_RoundTrips(bool configured, bool other)
    {
        var flow = MapEdge(r => r.StreamData = configured);
        Assert.Equal(configured, flow.Load.StreamData);
        Assert.NotEqual(other, flow.Load.StreamData);
    }

    [Fact]
    public void OmittedBooleanFlags_DefaultToDocumentedValues()
    {
        var flow = IngestionFlowMapper.FromLegacy(EdgeRow());
        Assert.True(flow.Load.StreamData);          // StreamData default true
        Assert.True(flow.OnErrorResume);            // OnErrorResume default true
        Assert.True(flow.SchemaSync.Sync);          // SyncSchema default true
        Assert.True(flow.Source.FilterIsAppend);    // srcFilterIsAppend default true
        Assert.False(flow.Load.SkipInsertNew);
        Assert.False(flow.Load.SkipUpdateExisting);
        Assert.False(flow.Target.TruncateBeforeLoad);
        Assert.False(flow.DeactivateFromBatch);
    }

    // ---------------------------------------------------------------------------------------------------
    // String fields: blank-to-null normalization for optional text.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankOptionalText_NormalizesToNull(string? blank)
    {
        var flow = MapEdge(r =>
        {
            r.srcFilter = blank;
            r.Description = blank;
            r.IdentityColumn = blank;
            r.PreProcessOnTrg = blank;
            r.DataSetColumn = blank;
        });

        Assert.Null(flow.Source.Filter);
        Assert.Null(flow.Description);
        Assert.Null(flow.Target.IdentityColumn);
        Assert.Null(flow.Process.PreProcessOnTarget);
        Assert.Null(flow.Source.DataSetColumn);
    }

    [Fact]
    public void NonBlankOptionalText_IsCarriedVerbatim_IncludingInternalWhitespace()
    {
        var flow = MapEdge(r => r.srcFilter = "Region = 'N A'");
        Assert.Equal("Region = 'N A'", flow.Source.Filter);
    }

    [Fact]
    public void BlankFlowType_FallsBackToIng_NonBlankIsKept()
    {
        Assert.Equal("ing", MapEdge(r => r.FlowType = "   ").FlowType);
        Assert.Equal("ingv", MapEdge(r => r.FlowType = "ingv").FlowType);
    }

    // ---------------------------------------------------------------------------------------------------
    // Virtual columns: filtering and blank-name tolerance.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void VirtualColumns_MultipleSameFlow_AllMapped_OtherFlowsExcluded()
    {
        var row = EdgeRow();
        var virtuals = new[]
        {
            new LegacyIngestionVirtualRow { VirtualID = 1, FlowID = 7, ColumnName = "A", SelectExp = "1" },
            new LegacyIngestionVirtualRow { VirtualID = 2, FlowID = 7, ColumnName = "B", SelectExp = "2" },
            new LegacyIngestionVirtualRow { VirtualID = 3, FlowID = 8, ColumnName = "C", SelectExp = "3" },
        };

        var flow = IngestionFlowMapper.FromLegacy(row, virtuals);
        Assert.Equal(new[] { "A", "B" }, flow.VirtualColumns.Select(v => v.Name).ToArray());
    }

    [Fact]
    public void VirtualColumn_BlankNameAndType_NormalizeToNull_ExpressionKept()
    {
        var row = EdgeRow();
        var virtuals = new[]
        {
            new LegacyIngestionVirtualRow { VirtualID = 1, FlowID = 7, ColumnName = "  ", DataType = "", SelectExp = "GETUTCDATE()" },
        };

        var v = Assert.Single(IngestionFlowMapper.FromLegacy(row, virtuals).VirtualColumns);
        Assert.Null(v.Name);
        Assert.Null(v.DataType);
        Assert.Equal("GETUTCDATE()", v.SelectExpression);
    }

    [Fact]
    public void VirtualColumn_NullSelectExp_FailsFast_NamingVirtualId()
    {
        var row = EdgeRow();
        var virtuals = new[] { new LegacyIngestionVirtualRow { VirtualID = 11, FlowID = 7, ColumnName = "X", SelectExp = null } };

        var ex = Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(row, virtuals));
        Assert.Contains("11", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoVirtualRows_YieldsEmptyVirtualColumnList()
    {
        var flow = IngestionFlowMapper.FromLegacy(EdgeRow(), Array.Empty<LegacyIngestionVirtualRow>());
        Assert.Empty(flow.VirtualColumns);
    }

    // ---------------------------------------------------------------------------------------------------
    // Guard rails.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NullRow_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => IngestionFlowMapper.FromLegacy(null!));

    // ---------------------------------------------------------------------------------------------------
    // Ingestion-mapping concern owned by the schema builder: the source-to-target name map under cleanup.
    // These complement the baseline builder tests (which assert a single rename) with collision/dedup and
    // the pass-through identity of the map when cleanup is off.
    // ---------------------------------------------------------------------------------------------------

    private static readonly IngestionSchemaBuilder EdgeBuilder = new(new DefaultColumnNameCleaner());

    private static SqlColumn EdgeSrcColumn(string name)
        => new() { Name = name, DataType = SqlDataType.Parse("int"), IsNullable = true };

    private static IngestionFlow EdgeSchemaFlow(SchemaSyncPolicy sync) => new()
    {
        FlowId = 1,
        Source = new IngestionSource { Server = "s", Table = RelationalObject.Parse("[D].[dbo].[S]") },
        Target = new IngestionTarget { Server = "t", Table = RelationalObject.Parse("[D].[dbo].[T]") },
        SchemaSync = sync,
        SystemColumns = new SystemColumnsPolicy { InsertedDate = false, UpdatedDate = false },
    };

    [Fact]
    public void Cleanup_TwoSourceNamesCleanToSameTarget_AreDeduplicated_AndBothMapped()
    {
        // "Order No" and "Order#No" both strip to "OrderNo"; the second is suffixed so no target collision,
        // and the source-to-target map keeps a distinct entry for each original name.
        var schema = EdgeBuilder.Build(
            new[] { EdgeSrcColumn("Order No"), EdgeSrcColumn("Order#No") },
            EdgeSchemaFlow(new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" }),
            forStaging: true);

        Assert.Equal("OrderNo", schema.SourceToTargetNames["Order No"]);
        Assert.Equal("OrderNo1", schema.SourceToTargetNames["Order#No"]);
        Assert.Contains(schema.Columns, c => c.Name == "OrderNo");
        Assert.Contains(schema.Columns, c => c.Name == "OrderNo1");
    }

    [Fact]
    public void Cleanup_NameThatCleansToEmpty_BecomesPlaceholder()
    {
        // A name made entirely of invalid characters cleans to the empty string and must fall back to the
        // documented placeholder rather than yielding an unnamed column.
        var schema = EdgeBuilder.Build(
            new[] { EdgeSrcColumn("###") },
            EdgeSchemaFlow(new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" }),
            forStaging: true);

        Assert.Equal("EmptyColumnName", schema.SourceToTargetNames["###"]);
    }

    [Fact]
    public void Cleanup_ReplacementCharacter_IsUsedInsteadOfRemoval()
    {
        var schema = EdgeBuilder.Build(
            new[] { EdgeSrcColumn("A B") },
            EdgeSchemaFlow(new SchemaSyncPolicy
            {
                CleanColumnNames = true,
                CleanColumnNameRegex = "[^A-Za-z0-9_]",
                ReplaceInvalidCharsWith = "_",
            }),
            forStaging: true);

        Assert.Equal("A_B", schema.SourceToTargetNames["A B"]);
    }

    [Fact]
    public void Cleanup_InvalidRegex_FailsFast_QuotingThePattern()
    {
        var ex = Assert.Throws<SqlFlowException>(() => EdgeBuilder.Build(
            new[] { EdgeSrcColumn("Id") },
            EdgeSchemaFlow(new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "([" }),
            forStaging: true));

        Assert.Contains("([", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCleanup_SourceNamesMapToThemselvesVerbatim()
    {
        // With cleanup disabled, even an awkward name is its own target name (identity map), so the bulk-copy
        // mapping is a pass-through.
        var schema = EdgeBuilder.Build(
            new[] { EdgeSrcColumn("Order No") },
            EdgeSchemaFlow(new SchemaSyncPolicy { CleanColumnNames = false }),
            forStaging: true);

        Assert.Equal("Order No", schema.SourceToTargetNames["Order No"]);
        Assert.Contains(schema.Columns, c => c.Name == "Order No");
    }

    [Fact]
    public void EmptySourceColumns_ProducesEmptyMap_AndNoSystemColumnsWhenAllOff()
    {
        var schema = EdgeBuilder.Build(
            Array.Empty<SqlColumn>(),
            EdgeSchemaFlow(new SchemaSyncPolicy()),
            forStaging: true);

        Assert.Empty(schema.SourceToTargetNames);
        Assert.Empty(schema.Columns);
    }

    [Fact]
    public void DedupSuffix_FormattedWithInvariantCulture()
    {
        // The numeric dedup suffix is rendered with InvariantCulture; assert the exact "1" form so a locale
        // with different digit shaping cannot drift the target name.
        var schema = EdgeBuilder.Build(
            new[] { EdgeSrcColumn("Dup"), EdgeSrcColumn("Dup") },
            EdgeSchemaFlow(new SchemaSyncPolicy { CleanColumnNames = true, CleanColumnNameRegex = "[^A-Za-z0-9_]" }),
            forStaging: true);

        var expectedSuffix = 1.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(schema.Columns, c => c.Name == "Dup" + expectedSuffix);
    }
}
