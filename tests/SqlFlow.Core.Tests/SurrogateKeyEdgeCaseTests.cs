using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Ingestion.Legacy;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Edge-case hardening for the surrogate-key surface: the legacy-to-V3 mapping
/// (IngestionFlowMapper.FromLegacy producing SurrogateKeySpec) and the spec model itself
/// (ConnectionReference derivation, three-part SurrogateTable parsing, positional KeyColumns/SKeyColumns
/// pairing, the PreProcess/PostProcess gate, and multi-spec handling). Every case here is a boundary the
/// baseline SurrogateKeyMappingTests, IngestionMappingEdgeCaseTests, and RelationalObjectTests do not already
/// assert. Pure and in-memory: no database, no clock, no randomness.
/// </summary>
public sealed class SurrogateKeyEdgeCaseTests
{
    // Distinctly named local builders so they cannot collide with helpers in sibling files in this namespace.
    private static LegacyIngestionRow SkEdgeFlow() => new()
    {
        FlowID = 42,
        srcServer = "srcSrv",
        srcDBSchTbl = "[Db].[dbo].[Src]",
        trgServer = "trgSrv",
        trgDBSchTbl = "[Db].[dbo].[Trg]",
    };

    private static LegacySurrogateKeyRow SkEdgeRow(Action<LegacySurrogateKeyRow>? configure = null)
    {
        var row = new LegacySurrogateKeyRow
        {
            SurrogateKeyID = 1,
            FlowID = 42,
            SurrogateDbSchTbl = "[DW].[dbo].[DimCust]",
            SurrogateColumn = "CustKey",
            KeyColumns = "CustId",
        };
        configure?.Invoke(row);
        return row;
    }

    private static SurrogateKeySpec MapSingleSk(Action<LegacySurrogateKeyRow>? configure = null)
    {
        var row = SkEdgeRow(configure);
        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [row]);
        return Assert.Single(flow.SurrogateKeys);
    }

    // ---------------------------------------------------------------------------------------------------
    // ConnectionReference: local (no Server) vs remote (alias). The spec stores Server verbatim (NullIfBlank,
    // no trim) but derives ConnectionReference as "@" + Server.Trim(). Both halves of that asymmetry matter.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void BlankSurrogateServer_IsLocal_ServerNull_AndNoConnectionReference(string? blank)
    {
        var spec = MapSingleSk(r => r.SurrogateServer = blank);

        Assert.Null(spec.Server);
        Assert.Null(spec.ConnectionReference);
    }

    [Fact]
    public void NonBlankSurrogateServer_IsRemote_WithAtPrefixedConnectionReference()
    {
        var spec = MapSingleSk(r => r.SurrogateServer = "prod");

        Assert.Equal("prod", spec.Server);
        Assert.Equal("@prod", spec.ConnectionReference);
    }

    [Fact]
    public void SurrogateServer_WithSurroundingWhitespace_KeptVerbatimButReferenceIsTrimmed()
    {
        // NullIfBlank does not trim, so Server keeps the padding; ConnectionReference trims before prefixing.
        var spec = MapSingleSk(r => r.SurrogateServer = "  prod  ");

        Assert.Equal("  prod  ", spec.Server);
        Assert.Equal("@prod", spec.ConnectionReference);
    }

    [Fact]
    public void SurrogateServer_InteriorWhitespace_IsPreservedInReference()
    {
        // Only the outer edges are trimmed; an interior space is part of the alias and survives the prefix.
        var spec = MapSingleSk(r => r.SurrogateServer = "prod east");

        Assert.Equal("@prod east", spec.ConnectionReference);
    }

    [Fact]
    public void SurrogateServer_EqualToTargetServer_StillBuildsAReference_MapperDoesNotCollapseLocalRemote()
    {
        // The mapper does not compare the surrogate alias to the flow target; "same alias" is still a reference.
        // (Whether that means local is the executor's concern, not the spec's.)
        var spec = MapSingleSk(r => r.SurrogateServer = "trgSrv");

        Assert.Equal("@trgSrv", spec.ConnectionReference);
    }

    [Theory]
    [InlineData("a", "@a")]
    [InlineData("Prod-01", "@Prod-01")]
    [InlineData("server.with.dots", "@server.with.dots")]
    [InlineData("ALL_CAPS", "@ALL_CAPS")]
    public void ConnectionReference_PrependsAt_PreservingAliasCharactersAndCase(string server, string expected)
    {
        var spec = MapSingleSk(r => r.SurrogateServer = server);

        Assert.Equal(expected, spec.ConnectionReference);
    }

    // ---------------------------------------------------------------------------------------------------
    // SKeyColumns: positional pairing with KeyColumns. The model documents "must have the same count" but the
    // mapper does not enforce it; both lists are carried verbatim. These tests pin the actual behavior.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SKeyColumns_OmittedEntirely_DefaultsToEmpty_MeaningSameNamesAsKeyColumns()
    {
        var spec = MapSingleSk(r => r.KeyColumns = "A,B");

        Assert.Equal(new[] { "A", "B" }, spec.KeyColumns);
        Assert.Empty(spec.SKeyColumns);
    }

    [Fact]
    public void SKeyColumns_BlankString_DefaultsToEmpty()
    {
        var spec = MapSingleSk(r =>
        {
            r.KeyColumns = "A,B";
            r.sKeyColumns = "   ";
        });

        Assert.Empty(spec.SKeyColumns);
    }

    [Fact]
    public void SKeyColumns_MatchingCount_PairsPositionally()
    {
        var spec = MapSingleSk(r =>
        {
            r.KeyColumns = "SrcA,SrcB";
            r.sKeyColumns = "DimA,DimB";
        });

        Assert.Equal(new[] { "SrcA", "SrcB" }, spec.KeyColumns);
        Assert.Equal(new[] { "DimA", "DimB" }, spec.SKeyColumns);
        Assert.Equal(spec.KeyColumns.Count, spec.SKeyColumns.Count);
    }

    [Fact]
    public void SKeyColumns_FewerThanKeyColumns_IsNotRejectedByTheMapper()
    {
        // Documented contract is equal counts, but the mapper does not validate it. A mismatch is carried
        // through as-is (a downstream concern). This asserts the current behavior and flags the gap.
        var spec = MapSingleSk(r =>
        {
            r.KeyColumns = "A,B,C";
            r.sKeyColumns = "X";
        });

        Assert.Equal(3, spec.KeyColumns.Count);
        Assert.Single(spec.SKeyColumns);
        Assert.NotEqual(spec.KeyColumns.Count, spec.SKeyColumns.Count);
    }

    [Fact]
    public void SKeyColumns_MoreThanKeyColumns_IsNotRejectedByTheMapper()
    {
        var spec = MapSingleSk(r =>
        {
            r.KeyColumns = "A";
            r.sKeyColumns = "X,Y,Z";
        });

        Assert.Single(spec.KeyColumns);
        Assert.Equal(3, spec.SKeyColumns.Count);
    }

    [Fact]
    public void SKeyColumns_DropEmptyTokens_CanSilentlyChangeThePairingCount()
    {
        // The list parser drops empty/whitespace tokens, so a stray comma in sKeyColumns shrinks the list and
        // (silently) breaks an otherwise-balanced positional pairing. Worth pinning so a parser change is noticed.
        var spec = MapSingleSk(r =>
        {
            r.KeyColumns = "A,B";
            r.sKeyColumns = "X,,Y,";
        });

        Assert.Equal(new[] { "A", "B" }, spec.KeyColumns);
        Assert.Equal(new[] { "X", "Y" }, spec.SKeyColumns);
    }

    [Fact]
    public void SKeyColumns_BracketedIdentifiers_AreUnbracketed_LikeKeyColumns()
    {
        var spec = MapSingleSk(r =>
        {
            r.KeyColumns = "[Src Id],[Sys]";
            r.sKeyColumns = "[Dim Id],[Source System]";
        });

        Assert.Equal(new[] { "Src Id", "Sys" }, spec.KeyColumns);
        Assert.Equal(new[] { "Dim Id", "Source System" }, spec.SKeyColumns);
    }

    // ---------------------------------------------------------------------------------------------------
    // KeyColumns parsing specific to surrogate config (bracket-aware comma split, dedup behavior).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void KeyColumns_BracketedCommaInsideIdentifier_IsNotSplit()
    {
        // A comma inside a [bracketed] identifier is part of the name, not a delimiter.
        var spec = MapSingleSk(r => r.KeyColumns = "[Last, First],Region");

        Assert.Equal(new[] { "Last, First", "Region" }, spec.KeyColumns);
    }

    [Fact]
    public void KeyColumns_BracketedDotInsideIdentifier_IsPreserved()
    {
        var spec = MapSingleSk(r => r.KeyColumns = "[a.b],[c.d]");

        Assert.Equal(new[] { "a.b", "c.d" }, spec.KeyColumns);
    }

    [Fact]
    public void KeyColumns_EscapedClosingBracket_IsUnescaped()
    {
        var spec = MapSingleSk(r => r.KeyColumns = "[Weird]]Name]");

        Assert.Equal(new[] { "Weird]Name" }, spec.KeyColumns);
    }

    [Fact]
    public void KeyColumns_PreservesDuplicatesAndCase_NoDedup()
    {
        var spec = MapSingleSk(r => r.KeyColumns = "Id,Id,id");

        Assert.Equal(new[] { "Id", "Id", "id" }, spec.KeyColumns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , , ")]
    public void EmptyOrBlankKeyColumns_FailFast_NamingTheRowAndFlow(string? keyColumns)
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapSingleSk(r => r.KeyColumns = keyColumns));

        Assert.Contains("KeyColumns", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FlowID 42", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // SurrogateColumn: required, blank-to-null then fail-fast. Unlike KeyColumns it is NOT run through the
    // list parser, so brackets are NOT stripped (it is stored verbatim after a blank check).
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void MissingSurrogateColumn_FailFast_NamingTheRow(string? surrogateColumn)
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapSingleSk(r => r.SurrogateColumn = surrogateColumn));

        Assert.Contains("SurrogateColumn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SurrogateColumn_BracketedValue_IsKeptVerbatim_NotUnbracketed()
    {
        // SurrogateColumn does not pass through the bracket-stripping list parser; whatever is given is stored.
        var spec = MapSingleSk(r => r.SurrogateColumn = "[Cust Key]");

        Assert.Equal("[Cust Key]", spec.SurrogateColumn);
    }

    [Fact]
    public void SurrogateColumn_InteriorWhitespace_IsKeptVerbatim()
    {
        var spec = MapSingleSk(r => r.SurrogateColumn = "Cust Key");

        Assert.Equal("Cust Key", spec.SurrogateColumn);
    }

    // ---------------------------------------------------------------------------------------------------
    // SurrogateTable: three-part parsing, error wrapping that names the SurrogateDbSchTbl column.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SurrogateTable_FourPart_DropsLeadingServerPart()
    {
        var spec = MapSingleSk(r => r.SurrogateDbSchTbl = "[srv].[DW].[dbo].[Dim]");

        Assert.Equal("DW", spec.SurrogateTable.Database);
        Assert.Equal("dbo", spec.SurrogateTable.Schema);
        Assert.Equal("Dim", spec.SurrogateTable.Name);
    }

    [Fact]
    public void SurrogateTable_UnbracketedAndPadded_TrimsEachPart()
    {
        var spec = MapSingleSk(r => r.SurrogateDbSchTbl = " DW . dbo . DimCust ");

        Assert.Equal("DW", spec.SurrogateTable.Database);
        Assert.Equal("dbo", spec.SurrogateTable.Schema);
        Assert.Equal("DimCust", spec.SurrogateTable.Name);
    }

    [Fact]
    public void SurrogateTable_BracketedDotsAndEscapedBrackets_RoundTripThroughQualifiedName()
    {
        var spec = MapSingleSk(r => r.SurrogateDbSchTbl = "[my.db].[dbo].[Weird]]Name]");

        Assert.Equal("my.db", spec.SurrogateTable.Database);
        Assert.Equal("Weird]Name", spec.SurrogateTable.Name);
        Assert.Equal("[my.db].[dbo].[Weird]]Name]", spec.SurrogateTable.QualifiedName);
    }

    [Theory]
    [InlineData("dbo.Dim")]   // two-part
    [InlineData("Dim")]       // one-part
    [InlineData("DW..Dim")]   // empty schema part
    public void SurrogateTable_FewerThanThreePartsOrEmptyPart_FailFast_NamingTheColumn(string name)
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapSingleSk(r => r.SurrogateDbSchTbl = name));

        Assert.Contains("SurrogateDbSchTbl", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SurrogateTable_BlankOrNull_FailFast_NamingTheColumn(string? name)
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapSingleSk(r => r.SurrogateDbSchTbl = name));

        Assert.Contains("SurrogateDbSchTbl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SurrogateTable_ParseError_WrapsInnerExceptionAndNamesTheFlow()
    {
        var ex = Assert.Throws<SqlFlowException>(() => MapSingleSk(r => r.SurrogateDbSchTbl = "dbo.Dim"));

        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<SqlFlowException>(ex.InnerException);
    }

    // ---------------------------------------------------------------------------------------------------
    // PreProcess / PostProcess: the model documents a "length > 2" gate, but the mapper only applies the
    // blank-to-null normalization. Short non-blank values therefore survive the mapping unchanged.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void PreAndPostProcess_Blank_NormalizeToNull(string? blank)
    {
        var spec = MapSingleSk(r =>
        {
            r.PreProcess = blank;
            r.PostProcess = blank;
        });

        Assert.Null(spec.PreProcess);
        Assert.Null(spec.PostProcess);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("ab")]
    public void PreAndPostProcess_ShortNonBlank_SurviveMapping_GateIsNotAppliedHere(string shortValue)
    {
        // The documented "gate length > 2" is the executor's responsibility, not the mapper's. The mapper
        // carries any non-blank value verbatim, so a one or two character hook is NOT dropped at mapping time.
        var spec = MapSingleSk(r =>
        {
            r.PreProcess = shortValue;
            r.PostProcess = shortValue;
        });

        Assert.Equal(shortValue, spec.PreProcess);
        Assert.Equal(shortValue, spec.PostProcess);
    }

    [Fact]
    public void PreAndPostProcess_NonBlank_AreCarriedVerbatim_IncludingWhitespaceAndCasing()
    {
        var spec = MapSingleSk(r =>
        {
            r.PreProcess = "  EXEC dbo.Prep @id = 1  ";
            r.PostProcess = "EXEC dbo.Done";
        });

        Assert.Equal("  EXEC dbo.Prep @id = 1  ", spec.PreProcess);
        Assert.Equal("EXEC dbo.Done", spec.PostProcess);
    }

    [Fact]
    public void PreAndPostProcess_OmittedByDefault_AreNull()
    {
        var spec = MapSingleSk();

        Assert.Null(spec.PreProcess);
        Assert.Null(spec.PostProcess);
    }

    // ---------------------------------------------------------------------------------------------------
    // Scalar field carry-through and defaults.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SpecCarriesIdentityFields_FromRowAndFlow()
    {
        var spec = MapSingleSk(r =>
        {
            r.SurrogateKeyID = 777;
            r.ToObjectMK = 12345;
        });

        Assert.Equal(777, spec.SurrogateKeyId);
        Assert.Equal(42, spec.FlowId);
        Assert.Equal(12345, spec.ToObjectMK);
    }

    [Fact]
    public void ToObjectMK_OmittedByDefault_IsNull()
    {
        var spec = MapSingleSk();

        Assert.Null(spec.ToObjectMK);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ToObjectMK_AnyIntValue_IsCarriedVerbatim_NoCoalescing(int toObjectMk)
    {
        var spec = MapSingleSk(r => r.ToObjectMK = toObjectMk);

        Assert.Equal(toObjectMk, spec.ToObjectMK);
    }

    // ---------------------------------------------------------------------------------------------------
    // FlowID filtering and multi-spec handling.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SurrogateKeys_NullEnumerable_YieldsNoSpecs()
    {
        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, null);

        Assert.Empty(flow.SurrogateKeys);
    }

    [Fact]
    public void SurrogateKeys_EmptyEnumerable_YieldsNoSpecs()
    {
        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, []);

        Assert.Empty(flow.SurrogateKeys);
    }

    [Fact]
    public void OnlyRowsMatchingTheFlowId_AreMapped_OthersFilteredOut()
    {
        var mine = SkEdgeRow(r => r.SurrogateKeyID = 1);
        var theirs = SkEdgeRow(r =>
        {
            r.SurrogateKeyID = 2;
            r.FlowID = 99; // different flow
        });

        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [mine, theirs]);

        var spec = Assert.Single(flow.SurrogateKeys);
        Assert.Equal(1, spec.SurrogateKeyId);
    }

    [Fact]
    public void MultipleMatchingRows_AllMapped_InSourceOrder()
    {
        var first = SkEdgeRow(r =>
        {
            r.SurrogateKeyID = 10;
            r.SurrogateColumn = "K1";
            r.SurrogateDbSchTbl = "[DW].[dbo].[DimA]";
        });
        var second = SkEdgeRow(r =>
        {
            r.SurrogateKeyID = 20;
            r.SurrogateColumn = "K2";
            r.SurrogateDbSchTbl = "[DW].[dbo].[DimB]";
            r.SurrogateServer = "prod";
        });

        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [first, second]);

        Assert.Equal(2, flow.SurrogateKeys.Count);
        Assert.Equal(10, flow.SurrogateKeys[0].SurrogateKeyId);
        Assert.Equal("DimA", flow.SurrogateKeys[0].SurrogateTable.Name);
        Assert.Null(flow.SurrogateKeys[0].ConnectionReference);
        Assert.Equal(20, flow.SurrogateKeys[1].SurrogateKeyId);
        Assert.Equal("DimB", flow.SurrogateKeys[1].SurrogateTable.Name);
        Assert.Equal("@prod", flow.SurrogateKeys[1].ConnectionReference);
    }

    [Fact]
    public void OneBadRowAmongMany_FailsFast_ForTheWholeMapping()
    {
        // The mapper materializes all matching rows eagerly; a single invalid row aborts the whole map.
        var good = SkEdgeRow(r => r.SurrogateKeyID = 1);
        var bad = SkEdgeRow(r =>
        {
            r.SurrogateKeyID = 2;
            r.KeyColumns = null; // invalid: no business-key columns
        });

        Assert.Throws<SqlFlowException>(() => IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [good, bad]));
    }

    [Fact]
    public void DuplicateSurrogateKeyIds_AreNotDeduplicated_BothMapped()
    {
        // The mapper does not enforce SurrogateKeyID uniqueness; two rows with the same id both map through.
        var a = SkEdgeRow(r => r.SurrogateKeyID = 5);
        var b = SkEdgeRow(r =>
        {
            r.SurrogateKeyID = 5;
            r.SurrogateDbSchTbl = "[DW].[dbo].[DimOther]";
        });

        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [a, b]);

        Assert.Equal(2, flow.SurrogateKeys.Count);
        Assert.All(flow.SurrogateKeys, s => Assert.Equal(5, s.SurrogateKeyId));
    }

    // ---------------------------------------------------------------------------------------------------
    // NullSurrogateKeyExecutor: the without-database default generates nothing regardless of configuration.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NullSurrogateKeyExecutor_ReturnsEmpty_EvenWhenTheFlowHasSpecs()
    {
        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [SkEdgeRow()]);

        var results = await NullSurrogateKeyExecutor.Instance.RunAsync(flow, "Server=ignored;");

        Assert.Empty(results);
    }

    [Fact]
    public async Task NullSurrogateKeyExecutor_HonorsAlreadyCancelledToken_ByStillReturningEmpty()
    {
        // The null executor does no work, so even a pre-cancelled token simply yields the empty result set.
        var flow = IngestionFlowMapper.FromLegacy(SkEdgeFlow(), null, [SkEdgeRow()]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var results = await NullSurrogateKeyExecutor.Instance.RunAsync(flow, "Server=ignored;", cts.Token);

        Assert.Empty(results);
    }

    [Fact]
    public void NullSurrogateKeyExecutor_InstanceIsSingleton()
    {
        Assert.Same(NullSurrogateKeyExecutor.Instance, NullSurrogateKeyExecutor.Instance);
    }
}
