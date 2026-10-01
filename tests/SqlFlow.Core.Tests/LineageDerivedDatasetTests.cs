using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A dataset a registered flow reads that is itself derived from other datasets (<see cref="DeclaredDerivation"/>): a
/// companion document the flow renders through, a model, a rule set. The derived dataset is a node the flow reads, and
/// its sources are reads of that node and of no flow, as a view's reads of its base tables are, so every flow reading
/// the dataset inherits them and is ordered after whatever writes a source. A wildcard source binds as a flow's own
/// wildcard read does, never to what a flow reading the dataset writes. Several flows declaring one derived dataset
/// share its node, and a declaration the graph cannot use skips the document with a warning naming its position. A
/// description also sees every registered document of the estate (<see cref="RegisteredLineageContext.Estate"/>). The
/// kind is the test-only <see cref="ProbeFlowKind"/>.
/// </summary>
public sealed class LineageDerivedDatasetTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string Store = "${env:PROBE_STORE}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-derived-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageDerivedDatasetTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private CollectionResult Collect() => new FlowSetCollector(YamlDocumentLoader.CreateDefault([new ProbeFlowKind()])).Collect(_root);

    private LineageReport Build(CollectionResult collected)
    {
        Assert.DoesNotContain(collected.Warnings, warning => warning.Contains("skipped", StringComparison.Ordinal));
        return LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines) + '\n';

    private static string Probe(string name, params string[] body)
        => Lines(["flowType: probe", $"name: {name}", "source: s3://drops/probe/", .. body]);

    /// <summary>A dataset of the store the flows write, as a flow-style map.</summary>
    private static string Stored(string relation, string name)
        => $"{{ relation: {relation}, system: probe-store, namespace: tenant1, group: master, name: \"{name}\", instance: \"{Store}\", separator: \":\" }}";

    /// <summary>The derived dataset the flows read: a model kept under the tenant, on no instance of its own.</summary>
    private static string Model(string name = "well-model", string relation = "reads")
        => $"{{ relation: {relation}, system: probe-model, namespace: tenant1, group: models, name: \"{name}\" }}";

    /// <summary>One derivation as the lines of a flow's <c>derivations</c> list.</summary>
    private static string[] Derivation(string dataset, params string[] from)
        => [$"  - dataset: {dataset}", "    from:", .. from.Select(source => "      - " + source)];

    private static string StoredKey(string name) => NodeKey.For(ServerIdentity.Dataset("probe-store", Store), "tenant1", "master", name);

    private static string ModelKey(string name = "well-model") => NodeKey.For(ServerIdentity.Dataset("probe-model", null), "tenant1", "models", name);

    private static string KeyOf(LineageFact fact) => NodeKey.For(fact.ServerRef, fact.Database, fact.Schema, fact.Name);

    /// <summary>What the derived dataset reads: the facts its own node carries, which no flow owns.</summary>
    private static List<string> SourcesOf(CollectionResult collected, string modelKey)
        => collected.Facts
            .Where(f => f.Flow is null && f.ViaModuleKey == modelKey && f.Relation == LineageRelation.Reads)
            .Select(f => f.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static bool DependsOn(LineageReport report, string from, string to)
        => report.FlowDependencies.Any(d => d.FromFlow == from && d.ToFlow == to);

    private static string SkippedWarning(CollectionResult collected)
    {
        Assert.Empty(collected.Flows);
        Assert.Empty(collected.Facts);
        var warning = Assert.Single(collected.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
        Assert.Contains("flows/probe.yaml", warning, StringComparison.Ordinal);
        return warning;
    }

    [Fact]
    public void A_derived_dataset_is_read_by_the_flow_and_its_sources_are_read_by_the_dataset()
    {
        Write("flows/writer.yaml", Probe("writer", "datasets:", "  - " + Stored("writes", "wks:unit:1.0.0")));
        Write("flows/reader.yaml", Probe("reader", ["derivations:", .. Derivation(Model(), Stored("reads", "WKS:unit:1.0.0"))]));

        var collected = Collect();
        var report = Build(collected);

        // The flow reads the derived dataset and nothing of what it is derived from: that read is the dataset's own.
        var read = Assert.Single(collected.Facts, f => f.Flow == "reader");
        Assert.Equal((LineageRelation.Reads, ModelKey(), LineageNodeKind.Dataset, LineageTier.Declared), (read.Relation, KeyOf(read), read.KindHint, read.Tier));
        Assert.Null(read.ViaModuleKey);
        var source = Assert.Single(collected.Facts, f => f.Flow is null);
        Assert.Equal((ModelKey(), LineageRelation.Reads, StoredKey("wks:unit:1.0.0"), LineageTier.Declared), (source.ViaModuleKey, source.Relation, KeyOf(source), source.Tier));

        // The derived dataset is a node of its own, on its system and under its namespace and group.
        var node = Assert.Single(report.Objects, o => o.Key == ModelKey());
        Assert.Equal((LineageNodeKind.Dataset, "tenant1", "models", "well-model"), (node.Kind, node.Database, node.Schema, node.Name));
        Assert.Equal("probe-model", ServerIdentity.DatasetSystem(node.Key));

        // The flow inherits the source through the dataset, so it runs after the flow writing it.
        Assert.Equal([["writer"], ["reader"]], report.ExecutionPlan.Waves.Select(w => w.Flows.ToList()).ToList());
        Assert.True(DependsOn(report, "writer", "reader"));
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == ModelKey() && e.Relation == LineageRelation.Reads && e.ObjectKey == StoredKey("wks:unit:1.0.0"));
        Assert.Contains(report.Edges, e => e.Flow == "reader" && e.ViaModule is null && e.Relation == LineageRelation.Reads && e.ObjectKey == ModelKey());
        Assert.Contains(report.Edges, e => e.Flow == "reader" && e.ViaModule == ModelKey() && e.Relation == LineageRelation.Reads && e.ObjectKey == StoredKey("wks:unit:1.0.0"));
        Assert.DoesNotContain(report.Edges, e => e.Flow == "reader" && e.ViaModule is null && e.ObjectKey == StoredKey("wks:unit:1.0.0"));
    }

    [Fact]
    public void A_wildcard_source_binds_to_what_the_estate_writes_and_never_to_what_a_flow_reading_the_dataset_writes()
    {
        Write("flows/writer.yaml", Probe(
            "writer",
            "datasets:",
            "  - " + Stored("writes", "wks:well:1.0.0"),
            "  - " + Stored("writes", "wks:log:1.0.0")));

        // Both readers share the dataset; one of them writes a dataset the pattern matches, which is its own output.
        Write("flows/reader.yaml", Probe(
            "reader",
            ["datasets:", "  - " + Stored("writes", "wks:well:2.0.0"), "derivations:", .. Derivation(Model(), Stored("reads", "wks:well:*"))]));
        Write("flows/second.yaml", Probe("second", ["derivations:", .. Derivation(Model(), Stored("reads", "wks:well:*"))]));

        var collected = Collect();
        var report = Build(collected);

        Assert.Equal(["wks:well:*", "wks:well:1.0.0"], SourcesOf(collected, ModelKey()));
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(DependsOn(report, "writer", "reader"));
        Assert.True(DependsOn(report, "writer", "second"));
        Assert.False(DependsOn(report, "reader", "second"));
        Assert.Contains(report.Objects, o => o.Kind == LineageNodeKind.Dataset && o.Name == "wks:well:*");
    }

    [Fact]
    public void Flows_declaring_one_derived_dataset_share_its_node_and_their_sources_add_up_each_once()
    {
        Write("flows/a.yaml", Probe("a", ["derivations:", .. Derivation(Model(), Stored("reads", "wks:unit:1.0.0"))]));
        Write("flows/b.yaml", Probe(
            "b",
            ["derivations:", .. Derivation(Model(), Stored("reads", "wks:unit:1.0.0"), Stored("reads", " WKS:Unit:1.0.0 "), Stored("reads", "wks:code:1.0.0"))]));

        var collected = Collect();
        var report = Build(collected);

        Assert.Equal(["wks:code:1.0.0", "wks:unit:1.0.0"], SourcesOf(collected, ModelKey()));
        Assert.Single(report.Objects, o => o.Key == ModelKey());

        // What one flow declares of the dataset is true of the dataset, so the other inherits it as well.
        foreach (var flow in new[] { "a", "b" })
        {
            Assert.Contains(report.Edges, e => e.Flow == flow && e.ViaModule == ModelKey() && e.ObjectKey == StoredKey("wks:code:1.0.0"));
            Assert.Contains(report.Edges, e => e.Flow == flow && e.ViaModule == ModelKey() && e.ObjectKey == StoredKey("wks:unit:1.0.0"));
        }
    }

    [Fact]
    public void A_derivation_without_sources_is_a_read_of_the_dataset_and_one_also_listed_as_a_dataset_is_one_fact()
    {
        Write("flows/probe.yaml", Probe("probe", "datasets:", "  - " + Model(), "derivations:", "  - dataset: " + Model(" Well-Model ")));

        var collected = Collect();

        var read = Assert.Single(collected.Facts);
        Assert.Equal(("probe", LineageRelation.Reads, ModelKey()), (read.Flow, read.Relation, KeyOf(read)));
    }

    [Fact]
    public void A_dataset_derived_from_a_derived_dataset_carries_its_sources_to_the_flow_reading_it()
    {
        Write("flows/writer.yaml", Probe("writer", "datasets:", "  - " + Stored("writes", "wks:unit:1.0.0")));
        Write("flows/reader.yaml", Probe(
            "reader",
            [
                "derivations:",
                .. Derivation(Model("report"), Model("well-model")),
                .. Derivation(Model("well-model"), Stored("reads", "wks:unit:1.0.0")),
            ]));

        var report = Build(Collect());

        Assert.True(DependsOn(report, "writer", "reader"));
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == ModelKey("report") && e.ObjectKey == ModelKey());
        Assert.Contains(report.Edges, e => e.Flow == "reader" && e.ViaModule == ModelKey() && e.ObjectKey == StoredKey("wks:unit:1.0.0"));
    }

    public static TheoryData<string[], string> Unusable => new()
    {
        { Derivation(Model(relation: "writes")), "declared derivation 1 has the relation 'Writes'; a flow reads the dataset it declares a derivation of" },
        { Derivation(Model(relation: "requires")), "declared derivation 1 has the relation 'Requires'; a flow declares only reads and writes" },
        { Derivation(Model("model-*")), "declared derivation 1 names 'model-*', a wildcard; a derived dataset is named" },
        { Derivation("{ relation: reads, system: \"Probe Model\", namespace: t, group: g, name: x }"), "declared derivation 1 names the system 'Probe Model'; a system is lower-case letters" },
        { ["  - from:", "      - " + Stored("reads", "wks:unit:1.0.0")], "declared derivation 1 is missing" },
        { Derivation(Model(), Stored("writes", "wks:unit:1.0.0")), "declared derivation 1 source 1 has the relation 'Writes'; a derived dataset only reads what it is derived from" },
        { Derivation(Model(), Stored("reads", "wks:unit:1.0.0"), Stored("reads", "a|b")), "declared derivation 1 source 2 has a name carrying a '|' or a control character" },
        { Derivation(Model(), Model(" WELL-MODEL ")), "declared derivation 1 source 1 is the derived dataset itself" },
        { [.. Derivation(Model(), Stored("reads", "wks:unit:1.0.0")), .. Derivation(Model("other"), "{ relation: reads, system: probe-store, namespace: \" \", group: g, name: x }")], "declared derivation 2 source 1 names no namespace" },
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public void An_unusable_derivation_skips_the_document_naming_its_position(string[] derivations, string expected)
    {
        Write("flows/probe.yaml", Probe("probe", ["derivations:", .. derivations]));

        var warning = SkippedWarning(Collect());

        Assert.Contains($"probe flow 'probe' {expected}", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_derived_identity_skips_the_document_without_echoing_a_literal_instance()
    {
        const string literal = "https://models.example.com/;Password=hunter2";
        var part = new string('x', DeclaredDataset.MaxPartLength);
        Write("flows/probe.yaml", Probe(
            "probe",
            ["derivations:", .. Derivation($"{{ relation: reads, system: probe-model, namespace: \"{part}\", group: \"{part}\", name: \"{part}\", instance: \"${{env:{new string('I', 200)}}}\" }}")]));
        Write("flows/literal.yaml", Probe(
            "literal",
            ["derivations:", .. Derivation($"{{ relation: reads, system: probe-model, namespace: tenant1, group: models, name: m, instance: \"{literal}\" }}")]));

        var collected = Collect();

        Assert.Contains(
            collected.Warnings,
            w => w.Contains("flows/probe.yaml: skipped", StringComparison.Ordinal)
                && w.Contains($"declared derivation 1 has an identity of", StringComparison.Ordinal)
                && w.Contains($"; the catalog keeps at most {DeclaredDataset.MaxIdentityLength}.", StringComparison.Ordinal));
        var fact = Assert.Single(collected.Facts, f => f.Flow == "literal");
        Assert.StartsWith("dataset:probe-model:inline:", fact.ServerRef, StringComparison.Ordinal);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("hunter2", StringComparison.Ordinal) || w.Contains("models.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void A_description_sees_every_registered_document_of_the_estate_in_path_order()
    {
        Write("flows/b.yaml", Probe("b", "estateEcho: true"));
        Write("flows/a.yaml", Probe("a"));
        Write("cache/z.yaml", Probe("z"));

        // Neither a built-in flow nor a file that is no flow document is a registered document.
        Write("load.yaml", Lines("name: load", "source:", "  type: json", "  location: ./in", "target:", "  connection: ${env:SQLFLOW_CONN_DWH}", "  schema: raw", "  table: Rows"));
        Write("flows/notes.yaml", Lines("just: text"));

        var collected = Collect();

        Assert.Contains("flows/b.yaml: the estate holds z.yaml=z, a.yaml=a, b.yaml=b.", collected.Warnings);
        Assert.Equal(["a", "b", "load", "z"], collected.Flows.Select(f => f.Node.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void A_document_described_on_its_own_sees_no_estate()
    {
        var context = new RegisteredLineageContext(Path.Combine(_root, "flows", "probe.yaml"), _root);

        Assert.Empty(context.Estate);
        Assert.Empty(RegisteredFlowLineage.Empty.Derivations);
    }
}
