using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The extractor specification, statement kind by statement kind: the DeltaForge semantics (operation-wise
/// context, CTE shadowing, two-phase effective-inbound resolution, CTAS staging rules, data-flow pairing
/// with the subquery/change-feed self-edge exemption) realized over ScriptDom's typed T-SQL AST.
/// </summary>
public sealed class LineageExtractorTests
{
    private static ScriptDependencies Extract(string sql, string? defaultDatabase = null)
        => TSqlLineageExtractor.Extract(sql, "test", defaultDatabase);

    private static IReadOnlyList<string> InboundKeys(ScriptDependencies deps)
        => deps.Inbound.Select(d => d.Table.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> OutboundKeys(ScriptDependencies deps)
        => deps.Outbound.Select(d => d.Table.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    // ---- Reads -----------------------------------------------------------------------------------------

    [Fact]
    public void Select_Joins_AreDirectReads()
    {
        var deps = Extract("SELECT * FROM DW.dbo.Orders o JOIN DW.dbo.Customers c ON c.Id = o.CustomerId;");

        Assert.Equal(["dw.dbo.customers", "dw.dbo.orders"], InboundKeys(deps));
        Assert.All(deps.Inbound, d => Assert.Equal(DependencyKind.Direct, d.Kind));
        Assert.Empty(deps.Warnings);
    }

    [Fact]
    public void Subqueries_InWhereExistsInAndSelectList_AreSubqueryReads()
    {
        var deps = Extract("""
            SELECT (SELECT MAX(Amount) FROM DW.dbo.Caps) AS Cap
            FROM DW.dbo.Orders o
            WHERE o.Id IN (SELECT OrderId FROM DW.dbo.Flagged)
              AND EXISTS (SELECT 1 FROM DW.dbo.Customers c WHERE c.Id = o.CustomerId)
              AND o.Amount > (SELECT AVG(Amount) FROM DW.dbo.History);
            """);

        Assert.Equal(
            ["dw.dbo.caps", "dw.dbo.customers", "dw.dbo.flagged", "dw.dbo.history", "dw.dbo.orders"],
            InboundKeys(deps));
        Assert.Equal(DependencyKind.Direct, deps.Inbound.Single(d => d.Table.Key == "dw.dbo.orders").Kind);
        Assert.Equal(DependencyKind.Subquery, deps.Inbound.Single(d => d.Table.Key == "dw.dbo.caps").Kind);
        Assert.Equal(DependencyKind.Subquery, deps.Inbound.Single(d => d.Table.Key == "dw.dbo.customers").Kind);
    }

    [Fact]
    public void DerivedTables_AreSubqueryDepth()
    {
        var deps = Extract("SELECT * FROM (SELECT * FROM DW.dbo.Orders) d;");

        Assert.Equal(DependencyKind.Subquery, deps.Inbound.Single().Kind);
    }

    [Fact]
    public void DirectReference_Dominates_SubqueryKind()
    {
        var deps = Extract("SELECT * FROM DW.dbo.Orders WHERE Id = (SELECT MAX(Id) FROM DW.dbo.Orders);");

        Assert.Equal(DependencyKind.Direct, deps.Inbound.Single().Kind);
    }

    // ---- CTEs ------------------------------------------------------------------------------------------

    [Fact]
    public void Ctes_ShadowRealObjects_AndDissolveInEffectiveInbound()
    {
        var deps = Extract("""
            WITH Orders AS (SELECT * FROM DW.dbo.RawOrders),
                 Enriched AS (SELECT * FROM Orders o JOIN DW.dbo.Customers c ON c.Id = o.CustomerId)
            INSERT INTO DW.dbo.Final SELECT * FROM Enriched;
            """);

        // The CTE names never become external reads; the chain dissolves to the base tables.
        Assert.Equal(["dw.dbo.customers", "dw.dbo.raworders"], InboundKeys(deps));
        Assert.Equal(["dw.dbo.final"], OutboundKeys(deps));
        Assert.Equal(
            ["dw.dbo.customers", "dw.dbo.raworders"],
            deps.EffectiveInbound().Select(t => t.Key).ToList());
    }

    [Fact]
    public void RecursiveCte_DoesNotReadItself()
    {
        var deps = Extract("""
            WITH Walk AS (
                SELECT Id, ParentId FROM DW.dbo.Nodes WHERE ParentId IS NULL
                UNION ALL
                SELECT n.Id, n.ParentId FROM DW.dbo.Nodes n JOIN Walk w ON w.Id = n.ParentId)
            SELECT * FROM Walk;
            """);

        Assert.Equal(["dw.dbo.nodes"], InboundKeys(deps));
    }

    // ---- Writes and movement pairs ----------------------------------------------------------------------

    [Fact]
    public void InsertSelect_PairsSourcesWithTarget()
    {
        var deps = Extract("INSERT INTO DW.dbo.Final SELECT * FROM DW.dbo.Stage s JOIN DW.dbo.Map m ON m.K = s.K;");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.final" && d.Operations.Contains(TableOperation.Insert));
        Assert.Equal(
            [("dw.dbo.map", "dw.dbo.final"), ("dw.dbo.stage", "dw.dbo.final")],
            deps.DataFlowPairs.Select(p => (p.Source.Key, p.Target.Key)).OrderBy(p => p.Item1, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void SelfPair_OnlySurvivesThroughSubquery()
    {
        // Direct self-feed: no pair (the DeltaForge exemption is subquery/change-feed ONLY).
        var direct = Extract("INSERT INTO DW.dbo.T SELECT * FROM DW.dbo.T;");
        Assert.Empty(direct.DataFlowPairs);

        // Subquery self-read: legitimate self-pair (watermark pattern).
        var subquery = Extract("INSERT INTO DW.dbo.T SELECT * FROM DW.dbo.S WHERE Id > (SELECT MAX(Id) FROM DW.dbo.T);");
        Assert.Contains(subquery.DataFlowPairs, p => p.Source.Key == "dw.dbo.t" && p.Target.Key == "dw.dbo.t");
        Assert.Contains(subquery.DataFlowPairs, p => p.Source.Key == "dw.dbo.s" && p.Target.Key == "dw.dbo.t");
    }

    [Fact]
    public void Pairs_RequireThreePartNames()
    {
        var deps = Extract("INSERT INTO Final SELECT * FROM dbo.Stage;");

        Assert.Empty(deps.DataFlowPairs);
        Assert.Equal(["dbo.stage"], InboundKeys(deps));
    }

    [Fact]
    public void Update_WithAliasedFrom_ResolvesTarget_AndPairsNothing()
    {
        var deps = Extract("""
            UPDATE a SET a.Total = s.Total
            FROM DW.dbo.Accounts a JOIN DW.dbo.Summary s ON s.AccountId = a.Id;
            """);

        var target = Assert.Single(deps.Outbound);
        Assert.Equal("dw.dbo.accounts", target.Table.Key);
        Assert.Contains(TableOperation.Update, target.Operations);

        // In-place: no movement pairs (no change feed read), but the local-deps entry IS recorded (the
        // reference's extract_update does), so the update target dissolves in phase two.
        Assert.Empty(deps.DataFlowPairs);
        Assert.Equal(
            ["dw.dbo.accounts", "dw.dbo.summary"],
            deps.LocalDeps["dw.dbo.accounts"].OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("dw.dbo.summary", deps.EffectiveInbound().Select(t => t.Key));
    }

    [Fact]
    public void Delete_IsInPlace_NoPairs()
    {
        var deps = Extract("DELETE FROM DW.dbo.Sessions WHERE ExpiresAt < GETUTCDATE();");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.sessions" && d.Operations.Contains(TableOperation.Delete));
        Assert.Empty(deps.DataFlowPairs);
    }

    [Fact]
    public void Merge_PairsUsingSourceWithTarget_AndOutputInto()
    {
        var deps = Extract("""
            MERGE INTO DW.dbo.Dim AS t
            USING DW.dbo.Stage AS s ON s.K = t.K
            WHEN MATCHED THEN UPDATE SET t.V = s.V
            WHEN NOT MATCHED THEN INSERT (K, V) VALUES (s.K, s.V)
            OUTPUT inserted.K INTO DW.dbo.Audit;
            """);

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.dim" && d.Operations.Contains(TableOperation.Merge));
        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.audit" && d.Operations.Contains(TableOperation.Insert));
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.stage" && p.Target.Key == "dw.dbo.dim");
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.stage" && p.Target.Key == "dw.dbo.audit");
    }

    [Fact]
    public void SelectInto_IsCtas_AndItsReadersPairNothing()
    {
        var deps = Extract("""
            SELECT * INTO DW.dbo.Scratch FROM DW.dbo.Source;
            INSERT INTO DW.dbo.Final SELECT * FROM DW.dbo.Scratch;
            """);

        Assert.Contains("dw.dbo.scratch", deps.CtasCreated);

        // Movement out of the CTAS target produces no pair (script-internal staging), but the chain still
        // dissolves to the true source in phase two.
        Assert.DoesNotContain(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.scratch");
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.source" && p.Target.Key == "dw.dbo.scratch");
        Assert.Contains("dw.dbo.source", deps.EffectiveInbound().Select(t => t.Key));
    }

    [Fact]
    public void StagingPattern_CreatedAndDropped_DissolvesEntirely()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.stg_run1 (Id int);
            INSERT INTO DW.dbo.stg_run1 SELECT Id FROM SRC.dbo.Orders;
            INSERT INTO DW.dbo.Final SELECT Id FROM DW.dbo.stg_run1;
            DROP TABLE DW.dbo.stg_run1;
            """);

        var facts = ScriptFactBuilder.Facts(deps, "flow", null, "@srv", LineageTier.Observed, minimumParts: 2).ToList();

        // The transient staging table never reaches the graph; the true movement does.
        Assert.DoesNotContain(facts, f => f.Name.Equals("stg_run1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(facts, f => f.Relation == LineageRelation.Writes && f.Name == "Final");
        Assert.Contains(facts, f => f.Relation == LineageRelation.Reads && f.Name == "Orders" && f.Database == "SRC");
    }

    [Fact]
    public void TempTables_NeverSurface()
    {
        var deps = Extract("""
            SELECT * INTO #t FROM DW.dbo.Source;
            INSERT INTO DW.dbo.Final SELECT * FROM #t;
            """);

        Assert.DoesNotContain(deps.EffectiveInbound(), t => t.IsTemp);
        Assert.Contains("dw.dbo.source", deps.EffectiveInbound().Select(t => t.Key));
        var facts = ScriptFactBuilder.Facts(deps, "flow", null, "@srv", LineageTier.Observed, 2).ToList();
        Assert.DoesNotContain(facts, f => f.Name.StartsWith('#'));
    }

    [Fact]
    public void TableVariables_AreInvisible()
    {
        var deps = Extract("""
            DECLARE @rows TABLE (Id int);
            INSERT INTO @rows SELECT Id FROM DW.dbo.Source;
            SELECT * FROM @rows;
            """);

        Assert.Equal(["dw.dbo.source"], InboundKeys(deps));
        Assert.Empty(deps.Outbound);
    }

    // ---- Modules ----------------------------------------------------------------------------------------

    [Fact]
    public void CreateView_RecordsCreation_AndItsBaseReads()
    {
        var deps = Extract("CREATE VIEW dbo.vw_Orders AS SELECT * FROM dbo.Orders o JOIN dbo.Lines l ON l.OrderId = o.Id;",
            defaultDatabase: "DW");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.vw_orders" && d.Operations.Contains(TableOperation.Create));
        Assert.Equal(["dw.dbo.lines", "dw.dbo.orders"], InboundKeys(deps));
        Assert.Equal(["dw.dbo.lines", "dw.dbo.orders"], deps.LocalDeps["dw.dbo.vw_orders"].OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void CreateProcedure_ExtractsItsBody()
    {
        var deps = Extract("""
            CREATE PROCEDURE dbo.usp_Refresh AS
            BEGIN
                TRUNCATE TABLE dbo.Mart;
                INSERT INTO dbo.Mart SELECT * FROM dbo.Fact;
            END
            """, defaultDatabase: "DW");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.usp_refresh" && d.Operations.Contains(TableOperation.Create));
        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.mart" && d.Operations.Contains(TableOperation.Truncate));
        Assert.Contains("dw.dbo.fact", InboundKeys(deps));
    }

    [Fact]
    public void InlineTvf_IsAParameterizedView()
    {
        var deps = Extract(
            "CREATE FUNCTION dbo.fn_Orders(@c int) RETURNS TABLE AS RETURN (SELECT * FROM dbo.Orders WHERE CustomerId = @c);",
            defaultDatabase: "DW");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.fn_orders");
        Assert.Contains("dw.dbo.orders", InboundKeys(deps));
    }

    [Fact]
    public void Trigger_PseudoTables_ReadTheParentAsChangeFeed()
    {
        var deps = Extract("""
            CREATE TRIGGER dbo.tr_Orders ON dbo.Orders AFTER DELETE AS
            BEGIN
                DELETE FROM dbo.OrderLines WHERE OrderId IN (SELECT Id FROM deleted);
            END
            """, defaultDatabase: "DW");

        var parent = deps.Inbound.Single(d => d.Table.Key == "dw.dbo.orders");
        Assert.Equal(DependencyKind.ChangeTracking, parent.Kind);

        // The propagating-delete carve-out: the change-feed read pairs with the in-place delete target.
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.orders" && p.Target.Key == "dw.dbo.orderlines");
    }

    [Fact]
    public void TableValuedFunctionCall_IsReadAndRequires()
    {
        var deps = Extract("SELECT * FROM DW.dbo.fn_Window(7) w JOIN DW.dbo.Orders o ON o.Day = w.Day;");

        var function = deps.Inbound.Single(d => d.Table.Key == "dw.dbo.fn_window");
        Assert.Contains(TableOperation.Read, function.Operations);
        Assert.Contains(TableOperation.Execute, function.Operations);
    }

    // ---- EXEC and the statically undecidable -------------------------------------------------------------

    [Fact]
    public void ExecStaticProcedure_IsARequiresDependency()
    {
        var deps = Extract("EXEC DW.dbo.usp_Rebuild;");

        var procedure = Assert.Single(deps.Inbound);
        Assert.Equal("dw.dbo.usp_rebuild", procedure.Table.Key);
        Assert.Contains(TableOperation.Execute, procedure.Operations);
        Assert.Empty(deps.Warnings);
    }

    [Theory]
    [InlineData("EXEC sp_executesql N'SELECT * FROM dbo.Hidden';", "sp_executesql")]
    [InlineData("EXEC ('SELECT * FROM dbo.Hidden');", "EXEC(...)")]
    public void DynamicSql_IsFlagged_NeverGuessed(string sql, string marker)
    {
        var deps = Extract(sql);

        Assert.Empty(deps.Inbound);
        Assert.Contains(deps.Warnings, w => w.Contains(marker, StringComparison.OrdinalIgnoreCase) || w.Contains("dynamic SQL", StringComparison.Ordinal));
    }

    [Fact]
    public void InsertExec_WritesTargetAndRequiresProcedure()
    {
        var deps = Extract("INSERT INTO DW.dbo.Results EXEC DW.dbo.usp_Compute;");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.results" && d.Operations.Contains(TableOperation.Insert));
        Assert.Contains(deps.Inbound, d => d.Table.Key == "dw.dbo.usp_compute" && d.Operations.Contains(TableOperation.Execute));
    }

    // ---- DDL relations -----------------------------------------------------------------------------------

    [Fact]
    public void DdlOperations_MapToLifecycleRelations()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.New1 (Id int);
            TRUNCATE TABLE DW.dbo.Old1;
            DROP TABLE DW.dbo.Gone1;
            ALTER TABLE DW.dbo.Changed1 ADD X int;
            """);

        var relations = deps.TypedRelations().ToLookup(r => r.Table.Key, r => r.Relation);

        // The lifecycle taxonomy: CREATE = Creates; TRUNCATE = Writes (plus the write-prerequisite Requires,
        // the table must already exist); DROP alone = Destroys ONLY; ALTER alone = Creates (the Modified
        // lifecycle: a structural source, exactly the reference).
        Assert.Equal([LineageRelation.Creates], relations["dw.dbo.new1"]);
        Assert.Equal([LineageRelation.Writes, LineageRelation.Requires], relations["dw.dbo.old1"]);
        Assert.Equal([LineageRelation.Destroys], relations["dw.dbo.gone1"]);
        Assert.Equal([LineageRelation.Creates], relations["dw.dbo.changed1"]);
    }

    // ---- Context and robustness ----------------------------------------------------------------------------

    [Fact]
    public void UseStatement_QualifiesLaterNames_ExplicitDatabaseWins()
    {
        var deps = Extract("""
            USE Staging;
            SELECT * FROM dbo.Incoming;
            SELECT * FROM DW.dbo.Existing;
            """);

        Assert.Equal(["dw.dbo.existing", "staging.dbo.incoming"], InboundKeys(deps));
    }

    [Fact]
    public void DefaultDatabase_AppliesToUnqualifiedNames()
    {
        var deps = Extract("SELECT * FROM dbo.Orders;", defaultDatabase: "DW");

        Assert.Equal(["dw.dbo.orders"], InboundKeys(deps));
    }

    [Fact]
    public void BracketedNames_WithEscapedBrackets_Parse()
    {
        var deps = Extract("SELECT * FROM [D]]W].[dbo].[Or[ders];");

        var table = Assert.Single(deps.Inbound).Table;
        Assert.Equal("D]W", table.Database);
        Assert.Equal("Or[ders", table.Name);
    }

    [Fact]
    public void FourPartName_CarriesTheLinkedServer()
    {
        var deps = Extract("SELECT * FROM LINKED.DW.dbo.Orders;");

        var table = Assert.Single(deps.Inbound).Table;
        Assert.Equal("LINKED", table.Server);
        Assert.Equal(4, table.PartCount);
    }

    [Fact]
    public void OpenQuery_IsAWarning()
    {
        var deps = Extract("SELECT * FROM OPENQUERY(LINKED, 'SELECT 1');");

        Assert.Contains(deps.Warnings, w => w.Contains("OPENQUERY", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangeTable_IsChangeTracking()
    {
        var deps = Extract("SELECT * FROM CHANGETABLE(CHANGES DW.dbo.Orders, 10) c;");

        Assert.Equal(DependencyKind.ChangeTracking, deps.Inbound.Single().Kind);
    }

    [Fact]
    public void ParseError_BecomesAWarning_NotAThrow()
    {
        var deps = Extract("SELEC * FROM;");

        Assert.Contains(deps.Warnings, w => w.Contains("parse error", StringComparison.Ordinal));
    }

    [Fact]
    public void UnhandledStatement_IsNamedInAWarning()
    {
        var deps = Extract("KILL 53;");

        Assert.Contains(deps.Warnings, w => w.Contains("KillStatement", StringComparison.Ordinal));
    }

    [Fact]
    public void LineageNeutralStatements_StayQuiet()
    {
        var deps = Extract("""
            SET NOCOUNT ON;
            BEGIN TRANSACTION;
            CREATE INDEX IX ON DW.dbo.T (Id);
            UPDATE STATISTICS DW.dbo.T;
            COMMIT;
            PRINT 'done';
            """);

        Assert.Empty(deps.Warnings);
        Assert.Empty(deps.Inbound);
        Assert.Empty(deps.Outbound);
    }

    [Fact]
    public void DepthGuard_DegradesToAWarning_NeverAnOverflow()
    {
        // ScriptDom itself rejects deeply parenthesized QUERIES, so the walker guard is exercised through
        // statement nesting, which the parser accepts to great depth.
        var sql = string.Concat(Enumerable.Repeat("BEGIN ", 80))
                  + "SELECT * FROM DW.dbo.Deep;"
                  + string.Concat(Enumerable.Repeat(" END", 80));

        var deps = Extract(sql);

        Assert.Contains(deps.Warnings, w => w.Contains("nesting beyond", StringComparison.Ordinal));
        Assert.Empty(deps.Inbound); // beyond the guard nothing is captured, by contract
    }

    [Fact]
    public void GoBatches_AllParticipate()
    {
        var deps = Extract("SELECT * FROM DW.dbo.A;\nGO\nSELECT * FROM DW.dbo.B;");

        Assert.Equal(["dw.dbo.a", "dw.dbo.b"], InboundKeys(deps));
    }

    [Fact]
    public void Extractor_IsThreadSafe_UnderParallelUse()
    {
        var results = Enumerable.Range(0, 64).AsParallel().WithDegreeOfParallelism(8)
            .Select(i => TSqlLineageExtractor.Extract($"INSERT INTO DW.dbo.T{i} SELECT * FROM DW.dbo.S{i};"))
            .ToList();

        for (var i = 0; i < results.Count; i++)
        {
            Assert.Single(results[i].DataFlowPairs);
            Assert.Empty(results[i].Warnings);
        }
    }
}
