using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Robustness edge cases for the operation-wise extractor, chosen to exercise corners the core specification
/// does not already pin: APPLY operators, set operators, write-through-CTE and write-through-derived-table
/// rejection, partition switch part-count gating, multi-statement chain dissolution through an uncreated
/// intermediate, rebuild versus transient-staging lifecycle, global temp objects, cursor bodies, BULK
/// INSERT, an EXEC of a procedure held in a variable, identity case folding, and the fact-builder part
/// threshold. Every case is pure in-memory (parse plus extract), so the suite always runs and stays
/// deterministic.
/// </summary>
public sealed class LineageExtractorEdgeCaseTests
{
    private static ScriptDependencies ExtractEdge(string sql, string? defaultDatabase = null)
        => TSqlLineageExtractor.Extract(sql, "edge", defaultDatabase);

    private static IReadOnlyList<string> InboundEdgeKeys(ScriptDependencies deps)
        => deps.Inbound.Select(d => d.Table.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> OutboundEdgeKeys(ScriptDependencies deps)
        => deps.Outbound.Select(d => d.Table.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> EffectiveEdgeKeys(ScriptDependencies deps)
        => deps.EffectiveInbound().Select(t => t.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static DependencyKind KindOf(ScriptDependencies deps, string key)
        => deps.Inbound.Single(d => d.Table.Key == key).Kind;

    // ---- APPLY operators -------------------------------------------------------------------------------

    [Fact]
    public void CrossApply_DerivedTable_IsSubqueryDepth()
    {
        var deps = ExtractEdge(
            "SELECT * FROM DW.dbo.Orders o CROSS APPLY (SELECT * FROM DW.dbo.Lines l WHERE l.OrderId = o.Id) x;");

        Assert.Equal(["dw.dbo.lines", "dw.dbo.orders"], InboundEdgeKeys(deps));
        Assert.Equal(DependencyKind.Direct, KindOf(deps, "dw.dbo.orders"));
        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.lines"));
    }

    [Fact]
    public void OuterApply_DerivedTable_IsSubqueryDepth()
    {
        var deps = ExtractEdge(
            "SELECT * FROM DW.dbo.A a OUTER APPLY (SELECT TOP 1 * FROM DW.dbo.B b WHERE b.K = a.K) x;");

        Assert.Equal(DependencyKind.Direct, KindOf(deps, "dw.dbo.a"));
        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.b"));
    }

    [Fact]
    public void CrossApply_TableValuedFunction_IsReadAndExecuteDirect()
    {
        var deps = ExtractEdge("SELECT * FROM DW.dbo.Orders o CROSS APPLY DW.dbo.fn_Lines(o.Id) l;");

        var function = deps.Inbound.Single(d => d.Table.Key == "dw.dbo.fn_lines");
        Assert.Contains(TableOperation.Read, function.Operations);
        Assert.Contains(TableOperation.Execute, function.Operations);
        Assert.Equal(DependencyKind.Direct, function.Kind);
    }

    // ---- Set operators ---------------------------------------------------------------------------------

    [Fact]
    public void UnionAll_InInsertSelect_PairsEveryBranchWithTarget()
    {
        var deps = ExtractEdge("INSERT INTO DW.dbo.T SELECT * FROM DW.dbo.A UNION ALL SELECT * FROM DW.dbo.B;");

        Assert.Equal(
            [("dw.dbo.a", "dw.dbo.t"), ("dw.dbo.b", "dw.dbo.t")],
            deps.DataFlowPairs.Select(p => (p.Source.Key, p.Target.Key))
                .OrderBy(p => p.Item1, StringComparer.Ordinal).ToList());
        Assert.Equal(["dw.dbo.a", "dw.dbo.b"], deps.LocalDeps["dw.dbo.t"].OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("SELECT * FROM DW.dbo.A UNION SELECT * FROM DW.dbo.B;")]
    [InlineData("SELECT * FROM DW.dbo.A EXCEPT SELECT * FROM DW.dbo.B;")]
    [InlineData("SELECT * FROM DW.dbo.A INTERSECT SELECT * FROM DW.dbo.B;")]
    [InlineData("SELECT * FROM DW.dbo.A UNION ALL SELECT * FROM DW.dbo.B UNION ALL SELECT * FROM DW.dbo.B;")]
    public void SetOperators_InSelect_ReadEveryBranchDirectly(string sql)
    {
        var deps = ExtractEdge(sql);

        Assert.Equal(["dw.dbo.a", "dw.dbo.b"], InboundEdgeKeys(deps));
        Assert.All(deps.Inbound, d => Assert.Equal(DependencyKind.Direct, d.Kind));
        Assert.Empty(deps.Outbound);
    }

    // ---- Nested subqueries and derived tables ----------------------------------------------------------

    [Fact]
    public void DeeplyNestedDerivedTables_ResolveToBase_AsSubquery()
    {
        var deps = ExtractEdge("SELECT * FROM (SELECT * FROM (SELECT * FROM DW.dbo.Deep) a) b;");

        var dependency = Assert.Single(deps.Inbound);
        Assert.Equal("dw.dbo.deep", dependency.Table.Key);
        Assert.Equal(DependencyKind.Subquery, dependency.Kind);
    }

    // ---- CTE corners -----------------------------------------------------------------------------------

    [Fact]
    public void Cte_NameShadowsOnlyOnePartReferences_NotSchemaQualified()
    {
        // The CTE label is "dbo"; a two-part read dbo.Orders must still bind the real object, never the CTE.
        var deps = ExtractEdge("WITH dbo AS (SELECT 1 AS x) SELECT * FROM dbo.Orders;", defaultDatabase: "DW");

        Assert.Equal(["dw.dbo.orders"], InboundEdgeKeys(deps));
        Assert.Empty(deps.Warnings);
    }

    [Fact]
    public void ValuesOnlyCte_RecordsNoLocalDep_AndDissolvesToNothing()
    {
        var deps = ExtractEdge("WITH v AS (SELECT * FROM (VALUES (1),(2)) t(n)) SELECT * FROM v;");

        Assert.Empty(deps.Inbound);
        Assert.Empty(deps.LocalDeps);
        Assert.Empty(EffectiveEdgeKeys(deps));
    }

    [Fact]
    public void Cte_SameNameInTwoStatements_KeepsLatestLocalDep_ButBothBasesSurface()
    {
        var deps = ExtractEdge("""
            WITH x AS (SELECT * FROM DW.dbo.A) SELECT * FROM x;
            WITH x AS (SELECT * FROM DW.dbo.B) SELECT * FROM x;
            """);

        // The local-dep map is keyed by the bare CTE name, so the second definition overwrites the first.
        Assert.Equal(["dw.dbo.b"], deps.LocalDeps["x"]);

        // Neither base is created by the script, so both come back through the standalone-read pass.
        Assert.Equal(["dw.dbo.a", "dw.dbo.b"], EffectiveEdgeKeys(deps));
    }

    // ---- Write-through rejection -----------------------------------------------------------------------

    [Fact]
    public void InsertThroughCte_IsWarned_AndProducesNoWriteTarget()
    {
        var deps = ExtractEdge("WITH c AS (SELECT * FROM DW.dbo.S) INSERT INTO c SELECT * FROM DW.dbo.X;");

        Assert.Empty(deps.Outbound);
        Assert.Contains(deps.Warnings,
            w => w.Contains("DML through CTE", StringComparison.Ordinal) && w.Contains("'c'", StringComparison.Ordinal));
        Assert.Equal(["dw.dbo.s", "dw.dbo.x"], InboundEdgeKeys(deps));
    }

    [Fact]
    public void UpdateThroughDerivedTableAlias_IsWarned_AndProducesNoWriteTarget()
    {
        var deps = ExtractEdge("UPDATE d SET d.V = 1 FROM (SELECT * FROM DW.dbo.T) d;");

        Assert.Empty(deps.Outbound);
        Assert.Contains(deps.Warnings, w => w.Contains("aliases a derived table", StringComparison.Ordinal));
        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.t"));
    }

    // ---- UPDATE / DELETE / MERGE read sides ------------------------------------------------------------

    [Fact]
    public void Update_SetFromScalarSubquery_NoFromClause_DissolvesToSubquerySource()
    {
        var deps = ExtractEdge("UPDATE DW.dbo.T SET V = (SELECT MAX(V) FROM DW.dbo.S);");

        var target = Assert.Single(deps.Outbound);
        Assert.Equal("dw.dbo.t", target.Table.Key);
        Assert.Contains(TableOperation.Update, target.Operations);

        // In place: no movement pair, but the subquery source is still a read and dissolves in phase two.
        Assert.Empty(deps.DataFlowPairs);
        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.s"));
        Assert.Equal(["dw.dbo.s"], EffectiveEdgeKeys(deps));
    }

    [Fact]
    public void Delete_WithJoinedFrom_ReadsBothSides_AndPairsNothing()
    {
        var deps = ExtractEdge("DELETE a FROM DW.dbo.Acct a JOIN DW.dbo.Bad b ON b.Id = a.Id;");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.acct" && d.Operations.Contains(TableOperation.Delete));
        Assert.Equal(["dw.dbo.acct", "dw.dbo.bad"], InboundEdgeKeys(deps));
        Assert.Empty(deps.DataFlowPairs);
        Assert.Equal(["dw.dbo.acct", "dw.dbo.bad"], deps.LocalDeps["dw.dbo.acct"].OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Merge_WithDerivedTableUsing_SourceIsSubquery_AndStillPairs()
    {
        var deps = ExtractEdge("""
            MERGE INTO DW.dbo.Dim t
            USING (SELECT K, V FROM DW.dbo.Stage) s ON s.K = t.K
            WHEN MATCHED THEN UPDATE SET t.V = s.V;
            """);

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.dim" && d.Operations.Contains(TableOperation.Merge));
        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.stage"));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.stage" && p.Target.Key == "dw.dbo.dim");
    }

    [Fact]
    public void Merge_NotMatchedBySource_Delete_StaysAMerge_AndPairsSource()
    {
        var deps = ExtractEdge("""
            MERGE INTO DW.dbo.D t USING DW.dbo.S s ON s.K = t.K
            WHEN NOT MATCHED BY SOURCE THEN DELETE;
            """);

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.d" && d.Operations.Contains(TableOperation.Merge));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.s" && p.Target.Key == "dw.dbo.d");
    }

    [Fact]
    public void Merge_InsertActionScalarSubquery_IsAReadThatPairs()
    {
        var deps = ExtractEdge("""
            MERGE INTO DW.dbo.D t USING DW.dbo.S s ON s.K = t.K
            WHEN NOT MATCHED THEN INSERT (V) VALUES ((SELECT MAX(V) FROM DW.dbo.Caps));
            """);

        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.caps"));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.caps" && p.Target.Key == "dw.dbo.d");
    }

    [Fact]
    public void OutputWithoutInto_AddsNoSecondTarget_AndCapturesNoReads()
    {
        var deps = ExtractEdge("DELETE FROM DW.dbo.T OUTPUT deleted.Id;");

        var target = Assert.Single(deps.Outbound);
        Assert.Equal("dw.dbo.t", target.Table.Key);
        Assert.Empty(deps.Inbound);
        Assert.Empty(deps.DataFlowPairs);
    }

    [Fact]
    public void InsertValues_WithScalarSubquery_PairsTheSubquerySource()
    {
        var deps = ExtractEdge("INSERT INTO DW.dbo.T (V) VALUES ((SELECT MAX(V) FROM DW.dbo.S));");

        Assert.Equal(DependencyKind.Subquery, KindOf(deps, "dw.dbo.s"));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.s" && p.Target.Key == "dw.dbo.t");
    }

    // ---- Four-part movement ----------------------------------------------------------------------------

    [Fact]
    public void FourPartToFourPart_InsertSelect_Pairs()
    {
        var deps = ExtractEdge("INSERT INTO LNK.DW.dbo.T SELECT * FROM LNK2.DW.dbo.S;");

        Assert.Equal(4, deps.Inbound.Single().Table.PartCount);
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "lnk2.dw.dbo.s" && p.Target.Key == "lnk.dw.dbo.t");
    }

    // ---- Module forms ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT * FROM dbo.T;", TableOperation.Create)]
    [InlineData("ALTER VIEW dbo.v AS SELECT * FROM dbo.T;", TableOperation.Alter)]
    public void ViewCreateOrAlterForms_CarryTheRightLifecycleOperation(string sql, TableOperation expected)
    {
        var deps = ExtractEdge(sql, defaultDatabase: "DW");

        var view = deps.Outbound.Single(d => d.Table.Key == "dw.dbo.v");
        Assert.Contains(expected, view.Operations);
        Assert.Equal(["dw.dbo.t"], InboundEdgeKeys(deps));
    }

    [Theory]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS BEGIN SELECT * FROM dbo.T; END", TableOperation.Create)]
    [InlineData("ALTER PROCEDURE dbo.p AS BEGIN SELECT * FROM dbo.T; END", TableOperation.Alter)]
    public void ProcedureCreateOrAlterForms_CarryTheRightLifecycleOperation(string sql, TableOperation expected)
    {
        var deps = ExtractEdge(sql, defaultDatabase: "DW");

        var module = deps.Outbound.Single(d => d.Table.Key == "dw.dbo.p");
        Assert.Contains(expected, module.Operations);

        // Either way the module is a structural source (Modified collapses into Created).
        var relations = deps.TypedRelations().Where(r => r.Table.Key == "dw.dbo.p").Select(r => r.Relation).ToList();
        Assert.Equal([LineageRelation.Creates], relations);
        Assert.Contains("dw.dbo.t", InboundEdgeKeys(deps));
    }

    [Fact]
    public void ViewOfView_AcrossGo_DissolvesToTheBaseOnly()
    {
        var deps = ExtractEdge("""
            CREATE VIEW dbo.v1 AS SELECT * FROM dbo.Base;
            GO
            CREATE VIEW dbo.v2 AS SELECT * FROM dbo.v1;
            """, defaultDatabase: "DW");

        Assert.Equal(["dw.dbo.v1", "dw.dbo.v2"], OutboundEdgeKeys(deps));

        // The intermediate view is created by the script, so phase two dissolves the chain down to the base.
        Assert.Equal(["dw.dbo.base"], EffectiveEdgeKeys(deps));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.base" && p.Target.Key == "dw.dbo.v1");
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.v1" && p.Target.Key == "dw.dbo.v2");
    }

    [Fact]
    public void ViewOfView_IntermediateIsBothReadAndCreated_InTypedRelations()
    {
        var deps = ExtractEdge("""
            CREATE VIEW dbo.v1 AS SELECT * FROM dbo.Base;
            GO
            CREATE VIEW dbo.v2 AS SELECT * FROM dbo.v1;
            """, defaultDatabase: "DW");

        var v1Relations = deps.TypedRelations().Where(r => r.Table.Key == "dw.dbo.v1")
            .Select(r => r.Relation).OrderBy(r => r).ToList();
        Assert.Equal([LineageRelation.Reads, LineageRelation.Creates], v1Relations);
    }

    // ---- Lifecycle: rebuild vs transient staging -------------------------------------------------------

    [Fact]
    public void DropThenCreateThenLoad_IsARebuild_NotCreatedThenDropped()
    {
        var deps = ExtractEdge("""
            DROP TABLE DW.dbo.T;
            GO
            CREATE TABLE DW.dbo.T (Id int);
            INSERT INTO DW.dbo.T SELECT Id FROM DW.dbo.S;
            """);

        // Drop precedes create, so this is a full rebuild: it is NOT the create-then-drop staging signature.
        Assert.DoesNotContain("dw.dbo.t", deps.CreatedThenDropped);

        // Refreshed lifecycle (drop + create + data) collapses to Writes only, with the existence prerequisite.
        var relations = deps.TypedRelations().Where(r => r.Table.Key == "dw.dbo.t")
            .Select(r => r.Relation).OrderBy(r => r).ToList();
        Assert.Equal([LineageRelation.Writes, LineageRelation.Requires], relations);
    }

    [Fact]
    public void CreateThenLoadThenDrop_IsRunScopedStaging_OmittedFromFacts()
    {
        var deps = ExtractEdge("""
            CREATE TABLE DW.dbo.T (Id int);
            INSERT INTO DW.dbo.T SELECT Id FROM DW.dbo.S;
            DROP TABLE DW.dbo.T;
            """);

        Assert.Contains("dw.dbo.t", deps.CreatedThenDropped);

        // The staging filter lives in the fact builder, so TypedRelations still carries the raw write, but the
        // facts hide the transient table entirely.
        var facts = ScriptFactBuilder.Facts(deps, "flow", null, "@srv", LineageTier.Observed, minimumParts: 2).ToList();
        Assert.DoesNotContain(facts, f => f.Name.Equals("T", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(facts, f => f.Relation == LineageRelation.Reads && f.Name == "S");
    }

    // ---- Multi-statement chain dissolution -------------------------------------------------------------

    [Fact]
    public void MultiStatementChain_DissolvesThroughAnUncreatedIntermediate()
    {
        var deps = ExtractEdge("""
            INSERT INTO DW.dbo.Mid SELECT * FROM DW.dbo.Src;
            INSERT INTO DW.dbo.Fin SELECT * FROM DW.dbo.Mid;
            """);

        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.src" && p.Target.Key == "dw.dbo.mid");
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.mid" && p.Target.Key == "dw.dbo.fin");

        // Mid was written but never CREATE'd by this script, so it survives the standalone-read pass alongside
        // the true source; the chain does not erase a real persisted table.
        Assert.Equal(["dw.dbo.mid", "dw.dbo.src"], EffectiveEdgeKeys(deps));
    }

    // ---- Partition switch ------------------------------------------------------------------------------

    [Fact]
    public void PartitionSwitch_ThreePart_MovesRowsSourceToTarget()
    {
        var deps = ExtractEdge("ALTER TABLE DW.dbo.Cur SWITCH PARTITION 1 TO DW.dbo.Archive PARTITION 1;");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.cur" && d.Operations.Contains(TableOperation.Delete));
        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.archive" && d.Operations.Contains(TableOperation.Insert));
        Assert.Equal(DependencyKind.Direct, KindOf(deps, "dw.dbo.cur"));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.cur" && p.Target.Key == "dw.dbo.archive");
    }

    [Fact]
    public void PartitionSwitch_TwoPart_RecordsMovement_ButGatesThePair()
    {
        // No default database, so both names stay two-part and fall under the three-part pair requirement.
        var deps = ExtractEdge("ALTER TABLE dbo.Cur SWITCH TO dbo.Archive;");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dbo.cur" && d.Operations.Contains(TableOperation.Delete));
        Assert.Contains(deps.Outbound, d => d.Table.Key == "dbo.archive" && d.Operations.Contains(TableOperation.Insert));
        Assert.Empty(deps.DataFlowPairs);

        // The local-dep movement is still recorded, so phase two can dissolve the archive to its source.
        Assert.Equal(["dbo.cur"], deps.LocalDeps["dbo.archive"]);
    }

    // ---- Truncate-then-load ----------------------------------------------------------------------------

    [Fact]
    public void TruncateThenInsert_SameTable_CombinesOperations_AndIsWrites()
    {
        var deps = ExtractEdge("""
            TRUNCATE TABLE DW.dbo.M;
            INSERT INTO DW.dbo.M SELECT * FROM DW.dbo.F;
            """);

        var target = deps.Outbound.Single(d => d.Table.Key == "dw.dbo.m");
        Assert.Contains(TableOperation.Truncate, target.Operations);
        Assert.Contains(TableOperation.Insert, target.Operations);

        var relations = deps.TypedRelations().Where(r => r.Table.Key == "dw.dbo.m")
            .Select(r => r.Relation).OrderBy(r => r).ToList();
        Assert.Equal([LineageRelation.Writes, LineageRelation.Requires], relations);
    }

    // ---- Synonyms (no static resolution) ---------------------------------------------------------------

    [Fact]
    public void Synonym_IsTreatedAsAPlainName_LeftForTheCatalogTier()
    {
        // The extractor does not know synonym targets; it surfaces the synonym name unchanged and quietly,
        // so the derived (catalog) tier can resolve it later.
        var deps = ExtractEdge("INSERT INTO DW.dbo.Final SELECT * FROM DW.dbo.syn_Orders;");

        Assert.Contains("dw.dbo.syn_orders", InboundEdgeKeys(deps));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.syn_orders" && p.Target.Key == "dw.dbo.final");
        Assert.Empty(deps.Warnings);
    }

    // ---- Temp objects ----------------------------------------------------------------------------------

    [Fact]
    public void GlobalTempTable_IsTemp_AndNeverSurfacesInEffectiveInbound()
    {
        var deps = ExtractEdge("""
            SELECT * INTO ##g FROM DW.dbo.Source;
            INSERT INTO DW.dbo.Final SELECT * FROM ##g;
            """);

        Assert.True(deps.Outbound.Single(d => d.Table.Key == "##g").Table.IsTemp);
        Assert.DoesNotContain(deps.EffectiveInbound(), t => t.IsTemp);
        Assert.Equal(["dw.dbo.source"], EffectiveEdgeKeys(deps));
    }

    [Fact]
    public void SelectIntoTemp_DissolvesThroughLocalDeps_ToTheTrueSource()
    {
        var deps = ExtractEdge("""
            SELECT * INTO #stage FROM DW.dbo.Source;
            INSERT INTO DW.dbo.Final SELECT * FROM #stage;
            """);

        // The temp is the CTAS target and the only path to the source; phase two dissolves it.
        Assert.Equal(["#stage"], deps.LocalDeps["dw.dbo.final"]);
        Assert.Equal(["dw.dbo.source"], EffectiveEdgeKeys(deps));
        var facts = ScriptFactBuilder.Facts(deps, "flow", null, "@srv", LineageTier.Observed, minimumParts: 2).ToList();
        Assert.DoesNotContain(facts, f => f.Name.StartsWith('#'));
    }

    // ---- Cursor body -----------------------------------------------------------------------------------

    [Fact]
    public void CursorOverSelect_ExtractsTheQueryReads_OpenFetchCloseAreNeutral()
    {
        var deps = ExtractEdge(
            "DECLARE c CURSOR FOR SELECT Id FROM DW.dbo.Orders; OPEN c; FETCH NEXT FROM c; CLOSE c; DEALLOCATE c;");

        Assert.Equal(["dw.dbo.orders"], InboundEdgeKeys(deps));
        Assert.Empty(deps.Outbound);
        Assert.Empty(deps.Warnings);
    }

    // ---- BULK INSERT -----------------------------------------------------------------------------------

    [Fact]
    public void BulkInsert_IsAWriteTarget_WithNoSource()
    {
        var deps = ExtractEdge("BULK INSERT DW.dbo.Target FROM 'c:\\data\\file.csv';");

        var target = Assert.Single(deps.Outbound);
        Assert.Equal("dw.dbo.target", target.Table.Key);
        Assert.Contains(TableOperation.BulkInsert, target.Operations);
        Assert.Empty(deps.Inbound);

        var relations = deps.TypedRelations().Select(r => r.Relation).OrderBy(r => r).ToList();
        Assert.Equal([LineageRelation.Writes, LineageRelation.Requires], relations);
    }

    // ---- EXEC of a variable-held procedure --------------------------------------------------------------

    [Fact]
    public void ExecOfProcedureHeldInVariable_IsFlagged_NeverGuessed()
    {
        var deps = ExtractEdge("DECLARE @p sysname = N'dbo.usp_X'; EXEC @p;");

        Assert.Empty(deps.Inbound);
        Assert.Empty(deps.Outbound);
        Assert.Contains(deps.Warnings, w => w.Contains("held in a variable", StringComparison.Ordinal));
    }

    [Fact]
    public void InsertExecDynamicString_WritesTarget_ButFlagsTheBody()
    {
        var deps = ExtractEdge("INSERT INTO DW.dbo.R EXEC ('SELECT 1');");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.r" && d.Operations.Contains(TableOperation.Insert));
        Assert.Empty(deps.Inbound);
        Assert.Contains(deps.Warnings, w => w.Contains("EXEC(...)", StringComparison.Ordinal));
    }

    // ---- Identity folding ------------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM Dw.DBO.ORDERS;")]
    [InlineData("SELECT * FROM [DW].[dbo].[ORDERS];")]
    [InlineData("SELECT * FROM dw.dbo.orders;")]
    public void Identity_IsCaseFolded_RegardlessOfSpellingOrBrackets(string sql)
    {
        var deps = ExtractEdge(sql);

        Assert.Equal(["dw.dbo.orders"], InboundEdgeKeys(deps));
    }

    [Fact]
    public void QuotedIdentifier_WithEmbeddedSpace_PreservesNameButFoldsKey()
    {
        var deps = ExtractEdge("SELECT * FROM [DW].[dbo].[Order Lines];");

        var table = Assert.Single(deps.Inbound).Table;
        Assert.Equal("Order Lines", table.Name);
        Assert.Equal("dw.dbo.order lines", table.Key);
    }

    // ---- Fact builder part threshold -------------------------------------------------------------------

    [Fact]
    public void FactBuilder_DropsNamesBelowTheMinimumPartThreshold()
    {
        // dbo.Final is two-part; dw.dbo.Src is three-part. With minimumParts 3 only the qualified one survives.
        var deps = ExtractEdge("INSERT INTO dbo.Final SELECT * FROM DW.dbo.Src;");

        var facts = ScriptFactBuilder.Facts(deps, "flow", null, "@srv", LineageTier.Observed, minimumParts: 3).ToList();

        Assert.DoesNotContain(facts, f => f.Name.Equals("Final", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(facts, f => f.Name == "Src" && f.Database == "DW" && f.Relation == LineageRelation.Reads);
    }
}
