using SqlFlow.Core.Lineage;
using SqlFlow.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Robustness edge cases for the lineage graph builder and service that the primary suites do not pin:
/// deeper diamond and chain wave shapes, multi-node and coexisting dependency cycles with fallback-wave
/// numbering, the module-inheritance depth ceiling and module self-reference, writes-only module traversal
/// boundaries, schema-side identity unification, multi-hop synonym resolution, subject resolution precedence
/// and arity bounds, and up/down traversal termination (leaf, unknown, self-exclusion). Pure in-memory:
/// every fact is constructed directly, every input is fixed, nothing touches a database, the clock, or disk.
/// </summary>
public sealed class LineageGraphEdgeCaseTests
{
    private static readonly DateTime EgGeneratedAt = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EgFileWrite = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CollectedFlow EgFlow(string name, string kind = "ing") => new()
    {
        Node = new LineageFlowNode { Name = name, Kind = kind, File = name + ".flow.yaml", Batch = null },
        TargetServerRef = "@dwh",
        FileWriteUtc = EgFileWrite,
    };

    private static LineageFact EgFact(
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

    private static LineageReport EgBuild(CollectionResult collected)
        => LineageGraphBuilder.Build(collected, "flows", [LineageTier.Declared], EgGeneratedAt);

    /// <summary>Builds an estate from (flow, relation, object) triples, one declared flow per distinct name.</summary>
    private static CollectionResult EgEstate(params (string Flow, LineageRelation Relation, string Object)[] facts)
    {
        var result = new CollectionResult();
        foreach (var name in facts.Select(f => f.Flow).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.Flows.Add(EgFlow(name));
        }

        foreach (var (flow, relation, objectName) in facts)
        {
            result.Facts.Add(EgFact(flow, relation, objectName));
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<string>> EgWaves(LineageReport report)
        => report.ExecutionPlan.Waves.Select(w => w.Flows).ToList();

    private static string EgKey(string name, string? db = "DW", string? schema = "dbo")
        => NodeKey.For("@dwh", db, schema, name);

    // ---- Argument validation at the trust boundary ---------------------------------------------------------

    [Fact]
    public void Build_NullCollected_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => LineageGraphBuilder.Build(null!, "flows", [LineageTier.Declared], EgGeneratedAt));

    [Fact]
    public void Build_NullTiers_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => LineageGraphBuilder.Build(new CollectionResult(), "flows", null!, EgGeneratedAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSubject_BlankSubject_Throws(string subject)
    {
        var report = EgBuild(EgEstate(("a", LineageRelation.Writes, "X")));
        Assert.Throws<ArgumentException>(() => LineageService.ResolveSubject(report, subject));
    }

    [Fact]
    public void ResolveSubject_NullReport_Throws()
        => Assert.Throws<ArgumentNullException>(() => LineageService.ResolveSubject(null!, "anything"));

    [Fact]
    public void Upstream_NullReport_Throws()
        => Assert.Throws<ArgumentNullException>(() => LineageService.Upstream(null!, "x"));

    [Fact]
    public void Downstream_NullReport_Throws()
        => Assert.Throws<ArgumentNullException>(() => LineageService.Downstream(null!, "x"));

    [Fact]
    public void ExplainFlow_NullReport_Throws()
        => Assert.Throws<ArgumentNullException>(() => LineageService.ExplainFlow(null!, "f"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExplainFlow_BlankName_Throws(string flowName)
    {
        var report = EgBuild(EgEstate(("a", LineageRelation.Writes, "X")));
        Assert.Throws<ArgumentException>(() => LineageService.ExplainFlow(report, flowName));
    }

    // ---- Empty and singleton estates -----------------------------------------------------------------------

    [Fact]
    public void EmptyCollection_BuildsAnEmptyPlan()
    {
        var report = EgBuild(new CollectionResult());

        Assert.Empty(report.Flows);
        Assert.Empty(report.Objects);
        Assert.Empty(report.Edges);
        Assert.Empty(report.ExecutionPlan.Waves);
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.Empty(report.Cycles);
        Assert.Empty(report.FlowDependencies);
    }

    [Fact]
    public void SingleIsolatedFlow_IsOneWaveWithNoDependencies()
    {
        var report = EgBuild(EgEstate(("solo", LineageRelation.Writes, "Only")));

        Assert.Equal([["solo"]], EgWaves(report));
        Assert.Empty(report.FlowDependencies);
        Assert.Empty(report.Cycles);
        Assert.Single(report.ExecutionPlan.Waves);
        Assert.Equal(1, report.ExecutionPlan.Waves[0].Wave);
    }

    [Fact]
    public void FlowWithNoFactsAtAll_StillPlansAsASingleWave()
    {
        // A document the collector found but extracted no endpoints from is still a schedulable node.
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("empty-flow", "sp"));

        var report = EgBuild(collected);

        Assert.Equal([["empty-flow"]], EgWaves(report));
        Assert.Empty(report.Objects);
    }

    // ---- Deeper wave shapes (sequential renumbering, contiguity) -------------------------------------------

    [Fact]
    public void LongDiamond_PlacesTheJoinAfterTheLongestArm()
    {
        // a -> (b -> c) and (d): the join e waits for the longer left arm, so it lands in wave 4, not wave 3.
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Writes, "Base"),
            ("b", LineageRelation.Reads, "Base"), ("b", LineageRelation.Writes, "L1"),
            ("c", LineageRelation.Reads, "L1"), ("c", LineageRelation.Writes, "L2"),
            ("d", LineageRelation.Reads, "Base"), ("d", LineageRelation.Writes, "R1"),
            ("e", LineageRelation.Reads, "L2"), ("e", LineageRelation.Reads, "R1")));

        Assert.Equal([["a"], ["b", "d"], ["c"], ["e"]], EgWaves(report));
    }

    [Fact]
    public void TwoIndependentChainsOfDifferentLength_ShareRenumberedWaves()
    {
        // Chain p->q->r (length 3) and chain x->y (length 2) are independent: the shorter chain's members
        // line up with the longer chain's earlier waves; no empty wave appears between them.
        var report = EgBuild(EgEstate(
            ("p", LineageRelation.Writes, "P"),
            ("q", LineageRelation.Reads, "P"), ("q", LineageRelation.Writes, "Q"),
            ("r", LineageRelation.Reads, "Q"),
            ("x", LineageRelation.Writes, "Xo"),
            ("y", LineageRelation.Reads, "Xo")));

        Assert.Equal([["p", "x"], ["q", "y"], ["r"]], EgWaves(report));
        // Waves are numbered contiguously from one.
        Assert.Equal([1, 2, 3], report.ExecutionPlan.Waves.Select(w => w.Wave).ToList());
    }

    [Fact]
    public void FanOutFromOneRoot_AllReadersShareWaveTwo()
    {
        var report = EgBuild(EgEstate(
            ("root", LineageRelation.Writes, "Seed"),
            ("c1", LineageRelation.Reads, "Seed"),
            ("c2", LineageRelation.Reads, "Seed"),
            ("c3", LineageRelation.Reads, "Seed"),
            ("c4", LineageRelation.Reads, "Seed")));

        Assert.Equal([["root"], ["c1", "c2", "c3", "c4"]], EgWaves(report));
    }

    [Fact]
    public void FanInToOneSink_SinkWaitsForEveryRoot()
    {
        var report = EgBuild(EgEstate(
            ("s1", LineageRelation.Writes, "A"),
            ("s2", LineageRelation.Writes, "B"),
            ("s3", LineageRelation.Writes, "C"),
            ("sink", LineageRelation.Reads, "A"), ("sink", LineageRelation.Reads, "B"), ("sink", LineageRelation.Reads, "C")));

        Assert.Equal([["s1", "s2", "s3"], ["sink"]], EgWaves(report));
        var froms = report.FlowDependencies.Where(d => d.ToFlow == "sink").Select(d => d.FromFlow)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(["s1", "s2", "s3"], froms);
    }

    // ---- Multi-node and coexisting cycles, fallback-wave numbering -----------------------------------------

    [Fact]
    public void ThreeFlowCycle_TracesAClosedPath_AndFallsBackTogether()
    {
        // a requires Xa (created by b); b requires Xb (created by c); c requires Xc (created by a).
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Creates, "Xc"), ("a", LineageRelation.Requires, "Xa"),
            ("b", LineageRelation.Creates, "Xa"), ("b", LineageRelation.Requires, "Xb"),
            ("c", LineageRelation.Creates, "Xb"), ("c", LineageRelation.Requires, "Xc")));

        var cycle = Assert.Single(report.Cycles);
        Assert.Equal(cycle.Flows[0], cycle.Flows[^1]);
        Assert.True(cycle.Flows.Count >= 4, "a closed three-node path repeats its head, so it lists four entries.");
        Assert.Equal(["a", "b", "c"], report.ExecutionPlan.Unordered);
        Assert.Equal([["a", "b", "c"]], EgWaves(report));
        Assert.Contains(report.Warnings, w => w.Contains("dependency cycle detected", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvedComponentBesideACycle_PutsTheCycleInTheFinalWave()
    {
        // A clean two-stage chain (load -> use) runs first; an independent two-flow cycle (p, q) degrades
        // into the fallback wave AFTER the resolved work, never blocking it.
        var report = EgBuild(EgEstate(
            ("load", LineageRelation.Writes, "Clean"),
            ("use", LineageRelation.Reads, "Clean"),
            ("p", LineageRelation.Creates, "Yp"), ("p", LineageRelation.Requires, "Yq"),
            ("q", LineageRelation.Creates, "Yq"), ("q", LineageRelation.Requires, "Yp")));

        var waves = EgWaves(report);
        Assert.Equal(["load"], waves[0]);
        Assert.Equal(["use"], waves[1]);
        Assert.Equal(["p", "q"], waves[^1]);
        Assert.Equal(3, waves.Count);
        Assert.Equal(["p", "q"], report.ExecutionPlan.Unordered);
    }

    [Fact]
    public void TwoDisjointCycles_AllMembersShareTheSingleFallbackWave()
    {
        // The fallback is one wave for ALL unprocessed members regardless of how many cycles they form.
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Creates, "Pa"), ("a", LineageRelation.Requires, "Pb"),
            ("b", LineageRelation.Creates, "Pb"), ("b", LineageRelation.Requires, "Pa"),
            ("c", LineageRelation.Creates, "Pc"), ("c", LineageRelation.Requires, "Pd"),
            ("d", LineageRelation.Creates, "Pd"), ("d", LineageRelation.Requires, "Pc")));

        Assert.Single(report.ExecutionPlan.Waves);
        Assert.Equal(["a", "b", "c", "d"], EgWaves(report)[0]);
        Assert.Equal(["a", "b", "c", "d"], report.ExecutionPlan.Unordered);
    }

    [Fact]
    public void CycleViaObjects_NameTheMediatingRequirement()
    {
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Creates, "Beta"), ("a", LineageRelation.Requires, "Alpha"),
            ("b", LineageRelation.Creates, "Alpha"), ("b", LineageRelation.Requires, "Beta")));

        var cycle = Assert.Single(report.Cycles);
        Assert.NotEmpty(cycle.ViaObjects);
        Assert.All(cycle.ViaObjects, v => Assert.StartsWith("@dwh|", v, StringComparison.Ordinal));
    }

    // ---- Reads dependency resolution corners ----------------------------------------------------------------

    [Fact]
    public void Reads_FallsBackToCreator_WhenTheOnlyWriterWouldDeadlock()
    {
        // X has a writer (w) that mutually cycles with the reader, AND a separate creator (mk). The writer
        // is skipped, so the reader must order after the creator instead of floating free.
        var report = EgBuild(EgEstate(
            ("reader", LineageRelation.Reads, "X"), ("reader", LineageRelation.Writes, "Y"),
            ("w", LineageRelation.Writes, "X"), ("w", LineageRelation.Reads, "Y"),
            ("mk", LineageRelation.Creates, "X")));

        var dependency = report.FlowDependencies.Where(d => d.ToFlow == "reader").ToList();
        var creatorDep = Assert.Single(dependency, d => d.FromFlow == "mk");
        Assert.Contains(EgKey("X"), creatorDep.ViaObjects);
        Assert.DoesNotContain(dependency, d => d.FromFlow == "w");
    }

    [Fact]
    public void Reads_DependsOnEveryWriter_NotJustOne()
    {
        // Two independent writers of X; the reader depends on BOTH and runs after both.
        var report = EgBuild(EgEstate(
            ("w1", LineageRelation.Writes, "X"),
            ("w2", LineageRelation.Writes, "X"),
            ("reader", LineageRelation.Reads, "X")));

        var froms = report.FlowDependencies.Where(d => d.ToFlow == "reader").Select(d => d.FromFlow)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(["w1", "w2"], froms);
        Assert.Equal([["w1", "w2"], ["reader"]], EgWaves(report));
    }

    [Fact]
    public void Reads_WithNoProducer_FloatsInWaveOneWithNoDependency()
    {
        // Nobody writes or creates the source: the reader cannot be ordered, so it stays a root.
        var report = EgBuild(EgEstate(
            ("orphan-reader", LineageRelation.Reads, "External"),
            ("other", LineageRelation.Writes, "Unrelated")));

        Assert.Equal([["orphan-reader", "other"]], EgWaves(report));
        Assert.Empty(report.FlowDependencies);
    }

    [Fact]
    public void Requires_DependsOnAllCreators_OfAMultiCreatedObject()
    {
        var report = EgBuild(EgEstate(
            ("c1", LineageRelation.Creates, "Shared"),
            ("c2", LineageRelation.Creates, "Shared"),
            ("runner", LineageRelation.Requires, "Shared")));

        var froms = report.FlowDependencies.Where(d => d.ToFlow == "runner").Select(d => d.FromFlow)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(["c1", "c2"], froms);
        Assert.Equal(["runner"], EgWaves(report)[^1]);
    }

    // ---- Destroys corners -----------------------------------------------------------------------------------

    [Fact]
    public void Destroys_WithNoOtherParticipants_IsItsOwnRoot()
    {
        // Nobody creates, writes, or reads the object: the destroyer has nothing to wait for.
        var report = EgBuild(EgEstate(
            ("dropper", LineageRelation.Destroys, "Ghost"),
            ("elsewhere", LineageRelation.Writes, "Other")));

        Assert.Equal([["dropper", "elsewhere"]], EgWaves(report));
        Assert.Empty(report.FlowDependencies);
    }

    [Fact]
    public void Destroys_WaitsForReadersToo_NotJustWriters()
    {
        // The reader has no other ordering; only Destroys-after-reader keeps it ahead of the drop.
        var report = EgBuild(EgEstate(
            ("reader", LineageRelation.Reads, "Scratch"),
            ("cleanup", LineageRelation.Destroys, "Scratch")));

        Assert.Equal([["reader"], ["cleanup"]], EgWaves(report));
        var dependency = Assert.Single(report.FlowDependencies);
        Assert.Equal(("reader", "cleanup"), (dependency.FromFlow, dependency.ToFlow));
    }

    // ---- Module relation inheritance corners ----------------------------------------------------------------

    [Fact]
    public void ModuleSelfReference_TerminatesWithoutDepthWarning()
    {
        // A view whose body reads itself (a degenerate definition): the visited-guard stops the walk, so the
        // reader still orders after the base writer and no spurious depth warning is emitted.
        var collected = EgEstate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reporter", LineageRelation.Reads, "vw_Loop"));

        var loop = NodeKey.For("@dwh", "DW", "dbo", "vw_Loop");
        collected.Facts.Add(EgFact(null, LineageRelation.Reads, "vw_Loop", viaModule: loop, tier: LineageTier.Derived));
        collected.Facts.Add(EgFact(null, LineageRelation.Reads, "Orders", viaModule: loop, tier: LineageTier.Derived));

        var report = EgBuild(collected);

        Assert.Equal([["loader"], ["reporter"]], EgWaves(report));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("module expansion beyond", StringComparison.Ordinal));
    }

    [Fact]
    public void ModuleChainBeyondTheCeiling_WarnsAndStopsInheriting()
    {
        // A view-on-view chain deeper than the inheritance ceiling: the builder warns and does NOT inherit
        // the base read at the bottom, so the reader never gains a dependency on the deep base's writer.
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("reporter"));
        collected.Flows.Add(EgFlow("deep-loader"));

        // The flow reads the head view.
        collected.Facts.Add(EgFact("reporter", LineageRelation.Reads, "vw_000"));

        // A long chain vw_000 -> vw_001 -> ... -> vw_NNN, each module reading the next.
        const int chainLength = LineageGraphBuilder.MaxModuleDepth + 4;
        for (var i = 0; i < chainLength; i++)
        {
            var moduleKey = NodeKey.For("@dwh", "DW", "dbo", $"vw_{i:000}");
            collected.Facts.Add(EgFact(null, LineageRelation.Reads, $"vw_{i + 1:000}", viaModule: moduleKey, tier: LineageTier.Derived));
        }

        // The deepest module finally reads a real base table that a loader writes.
        var deepBaseModule = NodeKey.For("@dwh", "DW", "dbo", $"vw_{chainLength:000}");
        collected.Facts.Add(EgFact(null, LineageRelation.Reads, "DeepBase", viaModule: deepBaseModule, tier: LineageTier.Derived));
        collected.Facts.Add(EgFact("deep-loader", LineageRelation.Writes, "DeepBase"));

        var report = EgBuild(collected);

        Assert.Contains(report.Warnings, w => w.Contains("module expansion beyond", StringComparison.Ordinal)
            && w.Contains("reporter", StringComparison.Ordinal));
        Assert.DoesNotContain(report.FlowDependencies, d => d.ToFlow == "reporter" && d.FromFlow == "deep-loader");
    }

    [Fact]
    public void ModuleChainExactlyAtTheCeiling_StillInherits()
    {
        // One short of the ceiling: the whole chain is inherited, so the reader DOES depend on the base writer
        // and no depth warning fires. This pins the boundary the over-limit test sits just past.
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("reporter"));
        collected.Flows.Add(EgFlow("base-loader"));
        collected.Facts.Add(EgFact("reporter", LineageRelation.Reads, "v_000"));

        const int chainLength = 6;
        for (var i = 0; i < chainLength; i++)
        {
            var moduleKey = NodeKey.For("@dwh", "DW", "dbo", $"v_{i:000}");
            collected.Facts.Add(EgFact(null, LineageRelation.Reads, $"v_{i + 1:000}", viaModule: moduleKey, tier: LineageTier.Derived));
        }

        var tailModule = NodeKey.For("@dwh", "DW", "dbo", $"v_{chainLength:000}");
        collected.Facts.Add(EgFact(null, LineageRelation.Reads, "RealBase", viaModule: tailModule, tier: LineageTier.Derived));
        collected.Facts.Add(EgFact("base-loader", LineageRelation.Writes, "RealBase"));

        var report = EgBuild(collected);

        Assert.DoesNotContain(report.Warnings, w => w.Contains("module expansion beyond", StringComparison.Ordinal));
        Assert.Contains(report.FlowDependencies, d => d.ToFlow == "reporter" && d.FromFlow == "base-loader");
    }

    [Fact]
    public void ModuleWritesAreNotTraversedFurther_OnlyReadsAndRequiresExpand()
    {
        // A procedure P writes module M; M's own body reads Base. Inheritance enqueues Reads/Requires keys
        // only, so inheriting "Writes M" must NOT pull in M's read of Base: the runner gains M as a write,
        // never a dependency on Base's loader.
        var collected = EgEstate(
            ("runner", LineageRelation.Requires, "usp_P"),
            ("base-loader", LineageRelation.Writes, "Base"));

        var procedure = NodeKey.For("@dwh", "DW", "dbo", "usp_P");
        var inner = NodeKey.For("@dwh", "DW", "dbo", "M");
        collected.Facts.Add(EgFact(null, LineageRelation.Writes, "M", viaModule: procedure, tier: LineageTier.Derived));
        collected.Facts.Add(EgFact(null, LineageRelation.Reads, "Base", viaModule: inner, tier: LineageTier.Derived));

        var report = EgBuild(collected);

        Assert.DoesNotContain(report.FlowDependencies, d => d.ToFlow == "runner" && d.FromFlow == "base-loader");
    }

    [Fact]
    public void ProcedureBodyWrite_MakesTheSpFlowABlockerForTheTargetsReader()
    {
        // Symmetric to the negative case above: a Writes inherited from a proc body DOES make downstream
        // readers of that target wait for the sp flow.
        var collected = EgEstate(
            ("runner", LineageRelation.Requires, "usp_Build"),
            ("consumer", LineageRelation.Reads, "Mart"));

        var procedure = NodeKey.For("@dwh", "DW", "dbo", "usp_Build");
        collected.Facts.Add(EgFact(null, LineageRelation.Writes, "Mart", viaModule: procedure, tier: LineageTier.Derived));

        var report = EgBuild(collected);

        Assert.Equal([["runner"], ["consumer"]], EgWaves(report));
        var dependency = Assert.Single(report.FlowDependencies, d => d.ToFlow == "consumer");
        Assert.Equal("runner", dependency.FromFlow);
    }

    // ---- Identity unification corners -----------------------------------------------------------------------

    [Fact]
    public void SchemalessReference_UnifiesWithTheSoleSchema()
    {
        // One fact gives db.name without a schema; another gives the full db.schema.name. With exactly one
        // candidate schema the schemaless reference is filled and the two collapse into one object.
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("writer"));
        collected.Flows.Add(EgFlow("reader"));
        collected.Facts.Add(EgFact("writer", LineageRelation.Writes, "Orders", db: "DW", schema: "dbo"));
        collected.Facts.Add(EgFact("reader", LineageRelation.Reads, "Orders", db: "DW", schema: null));

        var report = EgBuild(collected);

        Assert.Equal([["writer"], ["reader"]], EgWaves(report));
        Assert.Single(report.Objects, o => o.Name == "Orders");
    }

    [Fact]
    public void SchemalessReference_AmbiguousAcrossSchemas_StaysSplitAndWarns()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("writer"));
        collected.Facts.Add(EgFact("writer", LineageRelation.Writes, "Orders", db: "DW", schema: "dbo"));
        collected.Facts.Add(EgFact("writer", LineageRelation.Writes, "Orders", db: "DW", schema: "stg"));
        collected.Facts.Add(EgFact("writer", LineageRelation.Reads, "Orders", db: "DW", schema: null));

        var report = EgBuild(collected);

        Assert.Contains(report.Warnings, w => w.Contains("matches 2 schemas", StringComparison.Ordinal));
        Assert.Equal(3, report.Objects.Count(o => o.Name == "Orders"));
    }

    [Fact]
    public void NameOnlyReference_NeverUnifies_BecauseSchemaIsRequiredForTheAlias()
    {
        // A bare name (no database AND no schema) is not eligible for either alias branch, so it stays its
        // own node and gains no dependency on the fully-qualified writer.
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("writer"));
        collected.Flows.Add(EgFlow("reader"));
        collected.Facts.Add(EgFact("writer", LineageRelation.Writes, "Orders", db: "DW", schema: "dbo"));
        collected.Facts.Add(EgFact("reader", LineageRelation.Reads, "Orders", db: null, schema: null));

        var report = EgBuild(collected);

        Assert.Equal([["reader", "writer"]], EgWaves(report));
        Assert.Empty(report.FlowDependencies);
        Assert.Equal(2, report.Objects.Count(o => o.Name == "Orders"));
    }

    [Fact]
    public void UnifiedNode_TakesItsDatabaseFromTheFullyQualifiedSpelling()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("writer"));
        collected.Flows.Add(EgFlow("reader"));
        // writer: schema.name only (no database); reader: full three-part identity.
        collected.Facts.Add(EgFact("writer", LineageRelation.Writes, "Orders", db: null, schema: "dbo"));
        collected.Facts.Add(EgFact("reader", LineageRelation.Reads, "Orders", db: "Warehouse", schema: "dbo"));

        var report = EgBuild(collected);

        var node = Assert.Single(report.Objects, o => o.Name == "Orders");
        Assert.Equal("Warehouse", node.Database, ignoreCase: true);
        Assert.EndsWith("|warehouse|dbo|orders", node.Key, StringComparison.Ordinal);
    }

    // ---- Synonym resolution corners -------------------------------------------------------------------------

    [Fact]
    public void MultiHopSynonymChain_ResolvesToTheBase()
    {
        // syn_A -> syn_B -> syn_C -> Orders: a reader of syn_A must order after the Orders writer.
        var collected = EgEstate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reader", LineageRelation.Reads, "syn_A"));

        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_A",
            TargetDatabase = "DW", TargetSchema = "dbo", TargetName = "syn_B",
        });
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_B",
            TargetDatabase = "DW", TargetSchema = "dbo", TargetName = "syn_C",
        });
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_C",
            TargetDatabase = "DW", TargetSchema = "dbo", TargetName = "Orders",
        });

        var report = EgBuild(collected);

        Assert.Equal([["loader"], ["reader"]], EgWaves(report));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("synonym", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SynonymToADifferentSchema_RedirectsAcrossSchemas()
    {
        // The synonym lives in dbo but points at a table in another schema; the reader follows it there.
        var collected = EgEstate(("reader", LineageRelation.Reads, "syn_X"));
        collected.Flows.Add(EgFlow("loader"));
        collected.Facts.Add(EgFact("loader", LineageRelation.Writes, "Real", db: "DW", schema: "mart"));
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_X",
            TargetDatabase = "DW", TargetSchema = "mart", TargetName = "Real",
        });

        var report = EgBuild(collected);

        Assert.Equal([["loader"], ["reader"]], EgWaves(report));
    }

    // ---- Subject resolution corners -------------------------------------------------------------------------

    [Fact]
    public void ResolveSubject_FlowNameWins_OverAnIdenticallyNamedObject()
    {
        // A flow and an object share the literal name "Orders": the flow lookup happens first.
        var report = EgBuild(EgEstate(
            ("Orders", LineageRelation.Writes, "Orders"),
            ("downstream", LineageRelation.Reads, "Orders")));

        var resolved = LineageService.ResolveSubject(report, "Orders");
        Assert.Equal(["flow:Orders"], resolved);
    }

    [Fact]
    public void ResolveSubject_FlowNameIsCaseInsensitive()
    {
        var report = EgBuild(EgEstate(("Load-Orders", LineageRelation.Writes, "X")));
        Assert.Equal(["flow:Load-Orders"], LineageService.ResolveSubject(report, "load-orders"));
    }

    [Fact]
    public void ResolveSubject_ExactNodeKey_ReturnsThatKey()
    {
        var report = EgBuild(EgEstate(("w", LineageRelation.Writes, "Orders")));
        var key = EgKey("Orders");

        Assert.Equal([key], LineageService.ResolveSubject(report, key));
    }

    [Fact]
    public void ResolveSubject_SchemaQualifiedSuffix_MatchesOnSchemaAndName()
    {
        var report = EgBuild(EgEstate(("w", LineageRelation.Writes, "Orders")));

        var matched = LineageService.ResolveSubject(report, "dbo.Orders");
        Assert.Equal([EgKey("Orders")], matched);
        // The same name under a different schema would not match this suffix.
        Assert.Empty(LineageService.ResolveSubject(report, "mart.Orders"));
    }

    [Fact]
    public void ResolveSubject_ThreePartSuffix_MatchesOnDatabaseSchemaName()
    {
        var report = EgBuild(EgEstate(("w", LineageRelation.Writes, "Orders")));

        Assert.Equal([EgKey("Orders")], LineageService.ResolveSubject(report, "DW.dbo.Orders"));
        Assert.Empty(LineageService.ResolveSubject(report, "OtherDb.dbo.Orders"));
    }

    [Fact]
    public void ResolveSubject_FourPartSubject_IsNeverAMatch()
    {
        var report = EgBuild(EgEstate(("w", LineageRelation.Writes, "Orders")));
        Assert.Empty(LineageService.ResolveSubject(report, "srv.DW.dbo.Orders"));
    }

    [Fact]
    public void ResolveSubject_UnknownName_ReturnsEmpty()
    {
        var report = EgBuild(EgEstate(("w", LineageRelation.Writes, "Orders")));
        Assert.Empty(LineageService.ResolveSubject(report, "DoesNotExist"));
    }

    [Fact]
    public void ResolveSubject_BareNameAcrossSchemas_ReturnsAllMatchesSorted()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(EgFlow("w"));
        collected.Facts.Add(EgFact("w", LineageRelation.Writes, "Orders", schema: "dbo"));
        collected.Facts.Add(EgFact("w", LineageRelation.Writes, "Orders", schema: "stg"));

        var report = EgBuild(collected);
        var matches = LineageService.ResolveSubject(report, "Orders");

        Assert.Equal(2, matches.Count);
        Assert.Equal(matches.OrderBy(m => m, StringComparer.Ordinal), matches);
    }

    // ---- Upstream/Downstream traversal corners --------------------------------------------------------------

    [Fact]
    public void Downstream_OfATerminalObject_IsEmpty()
    {
        // FactOrders is only written, never read by another flow: nothing is downstream of it.
        var report = EgBuild(EgEstate(
            ("load", LineageRelation.Writes, "Orders"),
            ("build", LineageRelation.Reads, "Orders"), ("build", LineageRelation.Writes, "FactOrders")));

        Assert.Empty(LineageService.Downstream(report, EgKey("FactOrders")));
    }

    [Fact]
    public void Upstream_OfARootFlow_IsEmpty()
    {
        var report = EgBuild(EgEstate(
            ("load", LineageRelation.Writes, "Orders"),
            ("build", LineageRelation.Reads, "Orders")));

        Assert.Empty(LineageService.Upstream(report, "flow:load"));
    }

    [Fact]
    public void Downstream_NeverIncludesTheSubjectItself()
    {
        var report = EgBuild(EgEstate(
            ("load", LineageRelation.Writes, "Orders"),
            ("build", LineageRelation.Reads, "Orders")));

        var downstream = LineageService.Downstream(report, "flow:load");
        Assert.DoesNotContain("flow:load", downstream);
        Assert.Contains("flow:build", downstream);
    }

    [Fact]
    public void Traversal_OfAnUnknownSubject_IsEmptyBothWays()
    {
        var report = EgBuild(EgEstate(("load", LineageRelation.Writes, "Orders")));

        Assert.Empty(LineageService.Downstream(report, "nonexistent"));
        Assert.Empty(LineageService.Upstream(report, "nonexistent"));
    }

    [Fact]
    public void Downstream_OfADiamondRoot_ReachesEverythingBelow()
    {
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Writes, "Base"),
            ("b", LineageRelation.Reads, "Base"), ("b", LineageRelation.Writes, "Left"),
            ("c", LineageRelation.Reads, "Base"), ("c", LineageRelation.Writes, "Right"),
            ("d", LineageRelation.Reads, "Left"), ("d", LineageRelation.Reads, "Right")));

        var downstream = LineageService.Downstream(report, "flow:a");

        Assert.Contains("flow:b", downstream);
        Assert.Contains("flow:c", downstream);
        Assert.Contains("flow:d", downstream);
        Assert.Contains(EgKey("Left"), downstream);
        Assert.Contains(EgKey("Right"), downstream);
    }

    [Fact]
    public void Upstream_OfADiamondSink_ReachesBothArmsAndTheRoot()
    {
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Writes, "Base"),
            ("b", LineageRelation.Reads, "Base"), ("b", LineageRelation.Writes, "Left"),
            ("c", LineageRelation.Reads, "Base"), ("c", LineageRelation.Writes, "Right"),
            ("d", LineageRelation.Reads, "Left"), ("d", LineageRelation.Reads, "Right")));

        var upstream = LineageService.Upstream(report, "flow:d");

        Assert.Contains("flow:a", upstream);
        Assert.Contains("flow:b", upstream);
        Assert.Contains("flow:c", upstream);
        Assert.Contains(EgKey("Base"), upstream);
    }

    [Fact]
    public void Downstream_WalksThroughAViewModuleNode()
    {
        // base table -> view module -> the flow that reads the view: the module is an intermediate hop.
        var collected = EgEstate(
            ("loader", LineageRelation.Writes, "Orders"),
            ("reporter", LineageRelation.Reads, "vw_Orders"));

        var view = NodeKey.For("@dwh", "DW", "dbo", "vw_Orders");
        collected.Facts.Add(EgFact(null, LineageRelation.Reads, "Orders", viaModule: view, tier: LineageTier.Derived));

        var report = EgBuild(collected);
        var downstream = LineageService.Downstream(report, EgKey("Orders"));

        Assert.Contains(view, downstream);
        Assert.Contains("flow:reporter", downstream);
    }

    [Fact]
    public void ExplainFlow_ReportsCycleMembershipForFallbackFlows()
    {
        var report = EgBuild(EgEstate(
            ("a", LineageRelation.Creates, "Y"), ("a", LineageRelation.Requires, "X"),
            ("b", LineageRelation.Creates, "X"), ("b", LineageRelation.Requires, "Y")));

        var explain = LineageService.ExplainFlow(report, "a");

        Assert.NotNull(explain);
        Assert.True(explain!.InCycle);
        Assert.Equal("a", explain.Flow);
    }

    [Fact]
    public void ExplainFlow_OnAMidChainFlow_ListsBothDirections()
    {
        var report = EgBuild(EgEstate(
            ("load", LineageRelation.Writes, "Orders"),
            ("mid", LineageRelation.Reads, "Orders"), ("mid", LineageRelation.Writes, "Facts"),
            ("top", LineageRelation.Reads, "Facts")));

        var explain = LineageService.ExplainFlow(report, "mid");

        Assert.NotNull(explain);
        Assert.Equal(2, explain!.Wave);
        Assert.False(explain.InCycle);
        Assert.Contains(explain.DependsOn, d => d.Flow == "load");
        Assert.Contains(explain.RequiredBy, d => d.Flow == "top");
        Assert.Contains(explain.Reads, e => e.ObjectName.EndsWith("|dw|dbo|orders", StringComparison.Ordinal));
        Assert.Contains(explain.Writes, e => e.ObjectName.EndsWith("|dw|dbo|facts", StringComparison.Ordinal));
    }

    // ---- Determinism under reordered input ------------------------------------------------------------------

    [Fact]
    public void ShuffledFactOrder_ProducesTheSamePlanAndObjectOrder()
    {
        var forward = EgBuild(EgEstate(
            ("a", LineageRelation.Writes, "Base"),
            ("b", LineageRelation.Reads, "Base"), ("b", LineageRelation.Writes, "Mid"),
            ("c", LineageRelation.Reads, "Mid")));

        var reversed = EgBuild(EgEstate(
            ("c", LineageRelation.Reads, "Mid"),
            ("b", LineageRelation.Writes, "Mid"), ("b", LineageRelation.Reads, "Base"),
            ("a", LineageRelation.Writes, "Base")));

        Assert.Equal(EgWaves(forward), EgWaves(reversed));
        Assert.Equal(
            forward.Objects.Select(o => o.Key),
            reversed.Objects.Select(o => o.Key));
    }
}
