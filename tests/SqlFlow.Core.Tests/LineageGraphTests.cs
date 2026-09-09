using System.Text.Json;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The execution-order specification: the exact DeltaForge compute_schedule_run_order rules over the merged
/// graph: reads depend on writers with the mutual-cycle skip and the creator fallback, requires depends on
/// creators, destroys waits for everyone, modified Kahn levels become waves, cycle members land in a traced
/// fallback wave, multi-creator objects warn, and module expansion connects flows through view and procedure
/// bodies. Plus the V3 graph-hygiene rules: identity unification, synonym resolution, and byte-stable output.
/// </summary>
public sealed class LineageGraphTests
{
    private static CollectedFlow Flow(string name, string kind = "ing") => new()
    {
        Node = new LineageFlowNode { Name = name, Kind = kind, File = name + ".flow.yaml", Batch = null },
        TargetServerRef = "@dwh",
        FileWriteUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static LineageFact Fact(
        string? flow, LineageRelation relation, string name, string? db = "DW", string? schema = "dbo",
        string? viaModule = null, LineageTier tier = LineageTier.Declared) => new()
    {
        Flow = flow,
        ViaModuleKey = viaModule,
        Relation = relation,
        ServerRef = "@dwh",
        Database = db,
        Schema = schema,
        Name = name,
        Tier = tier,
    };

    private static LineageReport Build(CollectionResult collected)
        => LineageGraphBuilder.Build(collected, "flows", [LineageTier.Declared], new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc));

    private static CollectionResult Estate(params (string Flow, LineageRelation Relation, string Object)[] facts)
    {
        var result = new CollectionResult();
        foreach (var name in facts.Select(f => f.Flow).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.Flows.Add(Flow(name));
        }

        foreach (var (flow, relation, objectName) in facts)
        {
            result.Facts.Add(Fact(flow, relation, objectName));
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<string>> Waves(LineageReport report)
        => report.ExecutionPlan.Waves.Select(w => w.Flows).ToList();

    // ---- The ordering rules, one by one -------------------------------------------------------------------

    [Fact]
    public void Chain_OrdersIntoSequentialWaves()
    {
        var report = Build(Estate(
            ("load-orders", LineageRelation.Writes, "Orders"),
            ("build-facts", LineageRelation.Reads, "Orders"),
            ("build-facts", LineageRelation.Writes, "Facts"),
            ("refresh-mart", LineageRelation.Reads, "Facts")));

        Assert.Equal([["load-orders"], ["build-facts"], ["refresh-mart"]], Waves(report));
        Assert.Empty(report.Cycles);
    }

    [Fact]
    public void Diamond_RunsTheMiddleConcurrently()
    {
        var report = Build(Estate(
            ("a", LineageRelation.Writes, "Base"),
            ("b", LineageRelation.Reads, "Base"), ("b", LineageRelation.Writes, "Left"),
            ("c", LineageRelation.Reads, "Base"), ("c", LineageRelation.Writes, "Right"),
            ("d", LineageRelation.Reads, "Left"), ("d", LineageRelation.Reads, "Right")));

        Assert.Equal([["a"], ["b", "c"], ["d"]], Waves(report));
    }

    [Fact]
    public void IndependentFlows_AllRunInWaveOne()
    {
        var report = Build(Estate(
            ("a", LineageRelation.Writes, "X"),
            ("b", LineageRelation.Writes, "Y"),
            ("c", LineageRelation.Writes, "Z")));

        Assert.Equal([["a", "b", "c"]], Waves(report));
    }

    [Fact]
    public void MutualReadersAndWriters_SkipTheDeadlockingEdges()
    {
        // A writes X and reads Y; B writes Y and reads X: the DeltaForge mutual-cycle skip drops BOTH
        // edges, so the pair runs concurrently instead of deadlocking the plan.
        var report = Build(Estate(
            ("a", LineageRelation.Writes, "X"), ("a", LineageRelation.Reads, "Y"),
            ("b", LineageRelation.Writes, "Y"), ("b", LineageRelation.Reads, "X")));

        Assert.Equal([["a", "b"]], Waves(report));
        Assert.Empty(report.Cycles);
        Assert.Empty(report.FlowDependencies);
    }

    [Fact]
    public void Reads_FallBackToTheCreator_WhenNoSafeWriterExists()
    {
        // Nobody writes X; the sp flow's hook creates it. The reader still orders after the creator.
        var report = Build(Estate(
            ("creator", LineageRelation.Creates, "X"),
            ("reader", LineageRelation.Reads, "X")));

        Assert.Equal([["creator"], ["reader"]], Waves(report));
        var dependency = Assert.Single(report.FlowDependencies);
        Assert.Equal(("creator", "reader"), (dependency.FromFlow, dependency.ToFlow));
        Assert.Equal(["@dwh|dw|dbo|x"], dependency.ViaObjects);
    }

    [Fact]
    public void Requires_DependsOnTheCreator()
    {
        var report = Build(Estate(
            ("deploy", LineageRelation.Creates, "usp_Refresh"),
            ("runner", LineageRelation.Requires, "usp_Refresh")));

        Assert.Equal([["deploy"], ["runner"]], Waves(report));
    }

    [Fact]
    public void Destroys_WaitsForCreatorsWritersAndReaders()
    {
        var report = Build(Estate(
            ("loader", LineageRelation.Writes, "Scratch"),
            ("consumer", LineageRelation.Reads, "Scratch"),
            ("cleanup", LineageRelation.Destroys, "Scratch")));

        var waves = Waves(report);
        Assert.Equal(["cleanup"], waves[^1]);
        Assert.True(waves.Count == 3);
    }

    [Fact]
    public void CreatesAndWrites_ImplyNoDependencyByThemselves()
    {
        // Two writers of the same object have no derivable mutual order: both wave one (and the multi-
        // writer situation is the reader's problem, ordered after both).
        var report = Build(Estate(
            ("w1", LineageRelation.Writes, "X"),
            ("w2", LineageRelation.Writes, "X"),
            ("r", LineageRelation.Reads, "X")));

        Assert.Equal([["w1", "w2"], ["r"]], Waves(report));
    }

    [Fact]
    public void MultiCreator_Warns()
    {
        var report = Build(Estate(
            ("c1", LineageRelation.Creates, "X"),
            ("c2", LineageRelation.Creates, "X")));

        Assert.Contains(report.Warnings, w => w.Contains("created by multiple flows", StringComparison.Ordinal)
            && w.Contains("c1", StringComparison.Ordinal) && w.Contains("c2", StringComparison.Ordinal));
    }

    [Fact]
    public void SelfDependency_NeverHappens()
    {
        // The incremental pattern: a flow reads its own target (watermark) and writes it. No self-edge,
        // no cycle, one wave.
        var report = Build(Estate(
            ("incremental", LineageRelation.Writes, "T"),
            ("incremental", LineageRelation.Reads, "T")));

        Assert.Equal([["incremental"]], Waves(report));
        Assert.Empty(report.FlowDependencies);
    }

    [Fact]
    public void TrueCycle_TracesThePath_AndFallsBackLoudly()
    {
        // a requires X (created by b); b requires Y (created by a): undecidable by construction.
        var report = Build(Estate(
            ("a", LineageRelation.Creates, "Y"), ("a", LineageRelation.Requires, "X"),
            ("b", LineageRelation.Creates, "X"), ("b", LineageRelation.Requires, "Y")));

        var cycle = Assert.Single(report.Cycles);
        Assert.True(cycle.Flows.Count >= 3);
        Assert.Equal(cycle.Flows[0], cycle.Flows[^1]);
        Assert.Equal(["a", "b"], report.ExecutionPlan.Unordered);

        // Both still appear in the (single, fallback) wave: degrade loudly, never refuse to plan.
        Assert.Equal([["a", "b"]], Waves(report));
        Assert.Contains(report.Warnings, w => w.Contains("dependency cycle detected", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("fallback wave", StringComparison.Ordinal));
    }

    [Fact]
    public void CycleMembers_LandAfterTheResolvedWaves()
    {
        var report = Build(Estate(
            ("clean", LineageRelation.Writes, "Base"),
            ("a", LineageRelation.Reads, "Base"), ("a", LineageRelation.Creates, "Y"), ("a", LineageRelation.Requires, "X"),
            ("b", LineageRelation.Creates, "X"), ("b", LineageRelation.Requires, "Y")));

        var waves = Waves(report);
        Assert.Equal(["clean"], waves[0]);
        Assert.Equal(["a", "b"], waves[^1]);
    }

    // ---- Module expansion ----------------------------------------------------------------------------------

    [Fact]
    public void ViewExpansion_ConnectsTheReaderToTheBaseWriter()
    {
        var collected = Estate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reporter", LineageRelation.Reads, "vw_Orders"));

        // The derived tier: the view reads its base table.
        collected.Facts.Add(Fact(null, LineageRelation.Reads, "Orders",
            viaModule: NodeKey.For("@dwh", "DW", "dbo", "vw_Orders"), tier: LineageTier.Derived));

        var report = Build(collected);

        Assert.Equal([["loader"], ["reporter"]], Waves(report));
        var dependency = Assert.Single(report.FlowDependencies);
        Assert.Contains("@dwh|dw|dbo|orders", dependency.ViaObjects);
    }

    [Fact]
    public void ViewOnView_ExpandsTransitively()
    {
        var collected = Estate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reporter", LineageRelation.Reads, "vw_Outer"));

        collected.Facts.Add(Fact(null, LineageRelation.Reads, "vw_Inner",
            viaModule: NodeKey.For("@dwh", "DW", "dbo", "vw_Outer"), tier: LineageTier.Derived));
        collected.Facts.Add(Fact(null, LineageRelation.Reads, "Orders",
            viaModule: NodeKey.For("@dwh", "DW", "dbo", "vw_Inner"), tier: LineageTier.Derived));

        var report = Build(collected);

        Assert.Equal([["loader"], ["reporter"]], Waves(report));
    }

    [Fact]
    public void ProcedureExpansion_GivesTheSpFlowItsBodysWrites()
    {
        var collected = Estate(
            ("runner", LineageRelation.Requires, "usp_Build"),
            ("consumer", LineageRelation.Reads, "Mart"));

        // The proc body (derived): reads Fact, writes Mart.
        var procedure = NodeKey.For("@dwh", "DW", "dbo", "usp_Build");
        collected.Facts.Add(Fact(null, LineageRelation.Reads, "Fact1", viaModule: procedure, tier: LineageTier.Derived));
        collected.Facts.Add(Fact(null, LineageRelation.Writes, "Mart", viaModule: procedure, tier: LineageTier.Derived));

        var report = Build(collected);

        // The sp flow inherits the body's write, so the mart's reader waits for it.
        Assert.Equal([["runner"], ["consumer"]], Waves(report));

        // The procedure's derived writes AND reads are also attributed to the FLOW that executes it (in addition
        // to the module), so the mart traces back to the sp flow as its parent - what a data-flow view needs.
        var martKey = NodeKey.For("@dwh", "DW", "dbo", "Mart");
        var factKey = NodeKey.For("@dwh", "DW", "dbo", "Fact1");
        Assert.Contains(report.Edges, e =>
            e.Flow == "runner" && e.Relation == LineageRelation.Writes && e.ObjectKey == martKey
            && e.Tier == LineageTier.Derived && e.ViaModule == procedure);
        Assert.Contains(report.Edges, e =>
            e.Flow == "runner" && e.Relation == LineageRelation.Reads && e.ObjectKey == factKey
            && e.Tier == LineageTier.Derived && e.ViaModule == procedure);
        // The module-attributed edges (no flow) still exist too: the two provenances coexist.
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == procedure && e.ObjectKey == martKey);
    }

    [Fact]
    public void ModuleExpansion_GivesAViewReadingFlowTheViewsBaseReads()
    {
        // A flow that READS a view moves the view's base tables' data, so those reads are attributed to the FLOW
        // (in addition to the module): a graph consumer that follows only flow-attributed edges (the GUI's
        // project graph) keeps the chain writer -> table -> view -> reader connected instead of losing it at the
        // view hop and rendering the reader as a root.
        var collected = Estate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reporter", LineageRelation.Reads, "vw_Orders"));
        var view = NodeKey.For("@dwh", "DW", "dbo", "vw_Orders");
        collected.Facts.Add(Fact(null, LineageRelation.Reads, "Orders", viaModule: view, tier: LineageTier.Derived));

        var report = Build(collected);

        var ordersKey = NodeKey.For("@dwh", "DW", "dbo", "Orders");
        Assert.Contains(report.Edges, e =>
            e.Flow == "reporter" && e.Relation == LineageRelation.Reads && e.ObjectKey == ordersKey
            && e.Tier == LineageTier.Derived && e.ViaModule == view);
        // The module-attributed edge (no flow) still exists too: the two provenances coexist.
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == view && e.ObjectKey == ordersKey);
        // And the flow order follows the chain through the view.
        Assert.Equal([["loader"], ["reporter"]], Waves(report));
    }

    // ---- Identity, synonyms, determinism --------------------------------------------------------------------

    [Fact]
    public void TwoPartDeclared_UnifiesWithThreePartObserved_WhenUnambiguous()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("file-load", "file"));
        collected.Flows.Add(Flow("reader"));

        // The file flow declares schema.table (no database); the reader observed the full identity.
        collected.Facts.Add(Fact("file-load", LineageRelation.Writes, "Orders", db: null));
        collected.Facts.Add(Fact("reader", LineageRelation.Reads, "Orders", tier: LineageTier.Observed));

        var report = Build(collected);

        Assert.Equal([["file-load"], ["reader"]], Waves(report));
        Assert.Single(report.Objects, o => o.Name == "Orders");
    }

    [Fact]
    public void AmbiguousUnification_StaysSplit_AndWarns()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("file-load", "file"));
        collected.Facts.Add(Fact("file-load", LineageRelation.Writes, "Orders", db: null));
        collected.Facts.Add(Fact("file-load", LineageRelation.Reads, "Orders", db: "DW1"));
        collected.Facts.Add(Fact("file-load", LineageRelation.Reads, "Orders", db: "DW2"));

        var report = Build(collected);

        Assert.Contains(report.Warnings, w => w.Contains("matches 2 databases", StringComparison.Ordinal));
        Assert.Equal(3, report.Objects.Count(o => o.Name == "Orders"));
    }

    [Fact]
    public void DefaultDatabase_CompletesADatabaselessIdentity()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("file-load", "file"));
        collected.Flows.Add(Flow("reader"));
        collected.Servers.Add("@dwh", ("${env:SQLFLOW_CONN_DWH}", DataSourceKind.MSSQL));
        collected.ServerDefaultDatabases.Add("@dwh", "DW");

        // The file flow's target has no database (its connection carries it); the reader is fully qualified.
        collected.Facts.Add(Fact("file-load", LineageRelation.Writes, "Orders", db: null));
        collected.Facts.Add(Fact("reader", LineageRelation.Reads, "Orders", db: "DW"));

        var report = Build(collected);

        var node = Assert.Single(report.Objects, o => o.Name == "Orders");
        Assert.Equal("DW", node.Database, ignoreCase: true); // node metadata carries the case-folded key part
        Assert.Equal([["file-load"], ["reader"]], Waves(report));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("no database identity", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultDatabase_DisambiguatesTwoSameNamedDatabases()
    {
        // Two databases on one server both carry dbo.Orders. Single-candidate unification can only warn and
        // split; the connection's default catalog resolves the database-less write to the right one, exactly
        // as the engine resolves the two-part name at execution time.
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("loader-dw1"));
        collected.Flows.Add(Flow("loader-dw2", "file"));
        collected.Flows.Add(Flow("reader"));
        collected.Servers.Add("@dwh", ("${env:SQLFLOW_CONN_DWH}", DataSourceKind.MSSQL));
        collected.ServerDefaultDatabases.Add("@dwh", "DW2");

        collected.Facts.Add(Fact("loader-dw1", LineageRelation.Writes, "Orders", db: "DW1"));
        collected.Facts.Add(Fact("loader-dw2", LineageRelation.Writes, "Orders", db: null));
        collected.Facts.Add(Fact("reader", LineageRelation.Reads, "Orders", db: "DW2"));

        var report = Build(collected);

        Assert.DoesNotContain(report.Warnings, w => w.Contains("matches 2 databases", StringComparison.Ordinal));
        Assert.Equal(2, report.Objects.Count(o => o.Name == "Orders"));
        Assert.Equal([["loader-dw1", "loader-dw2"], ["reader"]], Waves(report));
    }

    [Fact]
    public void DatabaselessSqlServerIdentity_Warns_WhenItCannotBeCompleted()
    {
        // A SQL Server whose default catalog is unknown (unresolvable reference, no Initial Catalog) leaves
        // its two-part identities incomplete; the report must say so instead of silently splitting.
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("file-load", "file"));
        collected.Servers.Add("@dwh", ("${env:SQLFLOW_CONN_DWH}", DataSourceKind.MSSQL));
        collected.Facts.Add(Fact("file-load", LineageRelation.Writes, "Orders", db: null));

        var report = Build(collected);

        Assert.Contains(report.Warnings, w =>
            w.Contains("object 'dbo.Orders' on server '@dwh' has no database identity", StringComparison.Ordinal));
    }

    [Fact]
    public void Synonyms_ResolveToTheBaseObject()
    {
        var collected = Estate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reader", LineageRelation.Reads, "syn_Orders"));
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh",
            Database = "DW",
            Schema = "dbo",
            Name = "syn_Orders",
            TargetDatabase = "DW",
            TargetSchema = "dbo",
            TargetName = "Orders",
        });

        var report = Build(collected);

        Assert.Equal([["loader"], ["reader"]], Waves(report));
    }

    [Fact]
    public void SynonymCycle_WarnsAndStops()
    {
        var collected = Estate(("reader", LineageRelation.Reads, "syn_A"));
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_A",
            TargetDatabase = "DW", TargetSchema = "dbo", TargetName = "syn_B",
        });
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_B",
            TargetDatabase = "DW", TargetSchema = "dbo", TargetName = "syn_A",
        });

        var report = Build(collected);

        Assert.Contains(report.Warnings, w => w.Contains("synonym cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void ObservedStamp_SurvivesOnTheEdge()
    {
        var collected = Estate(("loader", LineageRelation.Writes, "Orders"));
        var runId = Guid.NewGuid();
        collected.Facts.Add(Fact("loader", LineageRelation.Writes, "Orders", tier: LineageTier.Observed) with
        {
            RunId = runId,
            ObservedAtUtc = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
        });

        var report = Build(collected);

        var observed = report.Edges.Single(e => e.Tier == LineageTier.Observed);
        Assert.Equal(runId, observed.ObservedRunId);
    }

    [Fact]
    public void SameInput_ProducesByteIdenticalReports()
    {
        LineageReport Compute() => Build(Estate(
            ("zeta", LineageRelation.Writes, "X"),
            ("alpha", LineageRelation.Reads, "X"), ("alpha", LineageRelation.Writes, "Y"),
            ("mid", LineageRelation.Reads, "X"), ("mid", LineageRelation.Writes, "Z"),
            ("omega", LineageRelation.Reads, "Y"), ("omega", LineageRelation.Reads, "Z")));

        var first = JsonSerializer.Serialize(Compute());
        var second = JsonSerializer.Serialize(Compute());

        Assert.Equal(first, second);
    }

    [Fact]
    public void LargeChain_ScalesAndStaysOrdered()
    {
        var facts = new List<(string, LineageRelation, string)>();
        for (var i = 0; i < 200; i++)
        {
            facts.Add(($"flow{i:000}", LineageRelation.Writes, $"T{i:000}"));
            if (i > 0)
            {
                facts.Add(($"flow{i:000}", LineageRelation.Reads, $"T{i - 1:000}"));
            }
        }

        var report = Build(Estate(facts.ToArray()));

        Assert.Equal(200, report.ExecutionPlan.Waves.Count);
        Assert.Empty(report.Cycles);
    }
}
