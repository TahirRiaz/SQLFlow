using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The full lineage contract of a registered flow kind (<see cref="RegisteredFlowDocument.DescribeLineage"/>): besides
/// database objects, a registered flow declares the file locations it reads, the file drops it lands, and the datasets
/// of external systems it reads and writes. File declarations join the estate's file reconciliation exactly as a file
/// ingestion's source and an invoke's drop do, anchored at the declaring document's folder; datasets become dataset
/// nodes that order their writers before their readers, a wildcard read binds to the datasets the estate writes, and
/// every declaration the graph cannot use skips the document with a warning that never echoes a literal instance. The
/// kind is the test-only <see cref="ProbeFlowKind"/>.
/// </summary>
public sealed class LineageRegisteredDescriptionTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private const string Store = "${env:PROBE_STORE}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-kind-describe-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageRegisteredDescriptionTests() => Directory.CreateDirectory(_root);

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

    private static string FileRead(string location, string pattern)
        => $"  - {{ relation: reads, location: \"{location}\", pattern: \"{pattern}\" }}";

    private static string FileWrite(string location, string? pattern = null)
        => pattern is null
            ? $"  - {{ relation: writes, location: \"{location}\" }}"
            : $"  - {{ relation: writes, location: \"{location}\", pattern: \"{pattern}\" }}";

    private static string Dataset(
        string relation, string name, string ns = "tenant1", string group = "master", string? instance = Store,
        string? separator = ":", string system = "probe-store")
    {
        var parts = new List<string> { $"relation: {relation}", $"system: \"{system}\"", $"namespace: \"{ns}\"", $"group: \"{group}\"", $"name: \"{name}\"" };
        if (instance is not null)
        {
            parts.Add($"instance: \"{instance}\"");
        }

        if (separator is not null)
        {
            parts.Add($"separator: \"{separator}\"");
        }

        return "  - { " + string.Join(", ", parts) + " }";
    }

    private static string Invoke(string name, string location)
        => Lines(
            "flowType: inv",
            $"name: {name}",
            "servicePrincipals:",
            "  deploy: { subscriptionId: s, resourceGroup: rg, dataFactoryName: adf }",
            "invoke:",
            "  type: adf",
            "  pipeline: pl_fetch",
            "  servicePrincipal: deploy",
            "  output:",
            $"    location: {location}",
            "    srcFile: \"*.csv\"");

    private static string JsonIngestion(string name, string location, string pattern)
        => Lines(
            $"name: {name}",
            "source:",
            "  type: json",
            $"  location: {location}",
            $"  options: {{ srcFile: \"{pattern}\" }}",
            "target:",
            "  connection: ${env:SQLFLOW_CONN_DWH}",
            "  schema: raw",
            "  table: Rows");

    private static IReadOnlyList<string> FileNames(LineageReport report)
        => report.Objects.Where(o => o.Kind == LineageNodeKind.File).Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static bool DependsOn(LineageReport report, string from, string to)
        => report.FlowDependencies.Any(d => d.FromFlow == from && d.ToFlow == to);

    private static string FileKey(string identity) => NodeKey.For(ServerIdentity.FileSystem, null, null, identity);

    private static List<string> DatasetReads(CollectionResult collected, string flow)
        => collected.Facts
            .Where(f => f.Flow == flow && f.Relation == LineageRelation.Reads && f.KindHint == LineageNodeKind.Dataset)
            .Select(f => $"{f.Database}/{f.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static string SkippedWarning(CollectionResult collected)
    {
        Assert.Empty(collected.Flows);
        Assert.DoesNotContain(collected.Facts, f => f.Flow == "probe");
        var warning = Assert.Single(collected.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
        Assert.Contains("flows/probe.yaml", warning, StringComparison.Ordinal);
        return warning;
    }

    [Fact]
    public void The_default_description_is_the_declared_objects()
    {
        var document = (ProbeFlowDocument)new ProbeFlowKind().Parse(
            Lines("name: probe", "source: s3://x/", "reads:", "  - { connection: \"${env:SQLFLOW_CONN_ODS}\", object: \"OdsDb.arc.Wells\" }"),
            "probe.yaml");

        var lineage = document.DescribeLineage(new RegisteredLineageContext(Path.Combine(_root, "probe.yaml"), _root));

        Assert.Equal(document.DeclaredObjects, lineage.Objects);
        Assert.Empty(lineage.Files);
        Assert.Empty(lineage.Datasets);
        Assert.Empty(lineage.Warnings);
        Assert.Empty(RegisteredFlowLineage.Empty.Objects);
    }

    [Fact]
    public void The_context_names_the_document_folder_and_bounds_the_estate()
    {
        var context = new RegisteredLineageContext(Path.Combine(_root, "flows", "probe.yaml"), _root);

        Assert.Equal(Path.Combine(_root, "flows"), context.DocumentFolder);
        Assert.True(context.Contains(_root));
        Assert.True(context.Contains(Path.Combine(_root, "flows", "..", "mappings", "m.yaml")));
        Assert.False(context.Contains(Path.Combine(_root, "..", "elsewhere.yaml")));
        Assert.False(context.Contains(_root + "-sibling"));
    }

    [Fact]
    public void A_registered_flow_reads_a_folder_relative_to_its_document_and_follows_the_flow_dropping_there()
    {
        Write("jobs/fetch.yaml", Invoke("fetch", "../data/in"));
        Write("flows/probe.yaml", Probe("probe", "files:", FileRead("../data/in", "*.csv")));

        var report = Build(Collect());

        Assert.Equal(["data/in"], FileNames(report));
        Assert.Contains(report.Edges, e => e.Flow == "probe" && e.Relation == LineageRelation.Reads && e.ObjectKey == FileKey("data/in"));
        Assert.True(DependsOn(report, "fetch", "probe"));
        Assert.Equal([["fetch"], ["probe"]], report.ExecutionPlan.Waves.Select(w => w.Flows.ToList()).ToList());
    }

    [Fact]
    public void A_registered_drop_feeds_the_file_ingestion_reading_its_files()
    {
        Write("flows/probe.yaml", Probe("probe", "files:", FileWrite("../lake/out", "part-*.jsonl")));
        Write("load.yaml", JsonIngestion("load", "./lake/out", "part-*.jsonl"));

        var report = Build(Collect());

        Assert.Equal(["lake/out"], FileNames(report));
        Assert.Contains(report.Edges, e => e.Flow == "probe" && e.Relation == LineageRelation.Writes && e.ObjectKey == FileKey("lake/out"));
        Assert.True(DependsOn(report, "probe", "load"));
    }

    [Fact]
    public void A_drop_nothing_reads_is_still_its_own_node()
    {
        Write("flows/probe.yaml", Probe("probe", "files:", FileWrite("../exports/wells")));

        var report = Build(Collect());

        Assert.Equal(["exports/wells"], FileNames(report));
        Assert.Contains(report.Edges, e => e.Flow == "probe" && e.Relation == LineageRelation.Writes && e.ObjectKey == FileKey("exports/wells"));
    }

    [Fact]
    public void A_location_is_cut_at_its_first_tokened_segment_and_a_reference_is_kept()
    {
        Write("flows/probe.yaml", Probe(
            "probe",
            "files:",
            FileWrite("../exports/{partition}/wells"),
            FileRead("${env:LAKE_ROOT}/in/{date:yyyy}", "*.csv")));

        var report = Build(Collect());

        Assert.Equal(["${env:LAKE_ROOT}/in", "exports"], FileNames(report));
    }

    [Fact]
    public void A_repeated_file_declaration_is_one_edge()
    {
        Write("flows/probe.yaml", Probe("probe", "files:", FileRead("../data/in", "*.csv"), FileRead("./../data/in/", "*.csv")));

        var collected = Collect();

        Assert.Single(collected.Facts, f => f.Flow == "probe" && f.Relation == LineageRelation.Reads);
    }

    [Theory]
    [InlineData("  - { relation: writes, location: \"{root}/out\" }", "declared file 1 has a location that is a token from its first segment")]
    [InlineData("  - { relation: reads, location: \"../data\", pattern: \"sub/*.csv\" }", "declared file 1 has the file pattern 'sub/*.csv', which names a folder")]
    [InlineData("  - { relation: reads, location: \"  \" }", "declared file 1 names no location")]
    [InlineData("  - { relation: requires, location: \"../data\" }", "declared file 1 has the relation 'Requires'; a flow declares only reads and writes")]
    public void An_unusable_file_declaration_skips_the_document(string declaration, string expected)
    {
        Write("flows/probe.yaml", Probe("probe", "files:", declaration));

        var warning = SkippedWarning(Collect());

        Assert.Contains($"probe flow 'probe' {expected}", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dataset_orders_its_writer_before_its_reader_and_is_a_dataset_node()
    {
        Write("flows/writer.yaml", Probe("writer", "datasets:", Dataset("writes", "wks:well:1.0.0")));
        Write("flows/reader.yaml", Probe("reader", "datasets:", Dataset("reads", "WKS:well:1.0.0")));

        var collected = Collect();
        var report = Build(collected);

        Assert.Equal([["writer"], ["reader"]], report.ExecutionPlan.Waves.Select(w => w.Flows.ToList()).ToList());
        var node = Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.Dataset);
        Assert.Equal(ServerIdentity.Dataset("probe-store", Store), node.ServerRef);
        Assert.Equal(("tenant1", "master"), (node.Database, node.Schema));
        Assert.Equal("probe-store", ServerIdentity.DatasetSystem(node.Key));
        Assert.Equal(NodeKey.For(ServerIdentity.Dataset("probe-store", Store), "tenant1", "master", "wks:well:1.0.0"), node.Key);
        Assert.DoesNotContain(collected.Servers.Keys, key => key.StartsWith(ServerIdentity.DatasetPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void A_dataset_without_an_instance_lives_on_its_system_alone()
    {
        Assert.Equal("dataset:probe-store", ServerIdentity.Dataset("probe-store", null));
        Assert.Equal("dataset:probe-store:" + ServerIdentity.From(Store), ServerIdentity.Dataset(" probe-store ", Store));
        Assert.Null(ServerIdentity.DatasetSystem(ServerIdentity.FileSystem));
        Assert.Equal("probe-store", ServerIdentity.DatasetSystem("dataset:probe-store"));
        Assert.Equal("probe-store", ServerIdentity.DatasetSystem("dataset:probe-store|tenant1|master|x"));
    }

    [Fact]
    public void A_wildcard_read_binds_segment_by_segment_to_the_datasets_written_on_its_instance_and_namespace()
    {
        Write("flows/writer.yaml", Probe(
            "writer",
            "datasets:",
            Dataset("writes", "wks:well:1.0.0"),
            Dataset("writes", "wks:well:2.0.0"),
            Dataset("writes", "wks:log:1.0.0"),
            Dataset("writes", "wks:well:extra:1.0.0"),
            Dataset("writes", "wks:well:1.0.0", ns: "tenant2"),
            Dataset("writes", "wks:well:1.1.0", instance: "${env:OTHER_STORE}")));
        Write("flows/reader.yaml", Probe("reader", "datasets:", Dataset("reads", "wks:well:1.*")));

        var collected = Collect();
        var report = Build(collected);

        Assert.Equal(["tenant1/wks:well:1.*", "tenant1/wks:well:1.0.0"], DatasetReads(collected, "reader"));
        Assert.True(DependsOn(report, "writer", "reader"));
        Assert.Contains(report.Objects, o => o.Kind == LineageNodeKind.Dataset && o.Name == "wks:well:1.*");
    }

    [Fact]
    public void A_wildcard_read_without_a_separator_matches_across_segments()
    {
        Write("flows/writer.yaml", Probe(
            "writer",
            "datasets:",
            Dataset("writes", "wks:well:1.0.0"),
            Dataset("writes", "wks:well:extra:1.0.0"),
            Dataset("writes", "wks:log:1.0.0")));
        Write("flows/reader.yaml", Probe("reader", "datasets:", Dataset("reads", "wks:well*", separator: null)));

        var collected = Collect();

        Assert.Equal(
            ["tenant1/wks:well*", "tenant1/wks:well:1.0.0", "tenant1/wks:well:extra:1.0.0"],
            DatasetReads(collected, "reader"));
    }

    [Fact]
    public void A_wildcard_read_never_binds_to_its_own_flow_and_keeps_its_node_when_nothing_matches()
    {
        Write("flows/cycle.yaml", Probe("cycle", "datasets:", Dataset("writes", "wks:well:1.0.0"), Dataset("reads", "wks:well:*")));
        Write("flows/lonely.yaml", Probe("lonely", "datasets:", Dataset("reads", "wks:nothing:*")));

        var collected = Collect();
        var report = Build(collected);

        Assert.Equal(["tenant1/wks:well:*"], DatasetReads(collected, "cycle"));
        Assert.Equal(["tenant1/wks:nothing:*"], DatasetReads(collected, "lonely"));
        Assert.Empty(report.ExecutionPlan.Unordered);
    }

    [Fact]
    public void A_repeated_dataset_declaration_is_one_fact()
    {
        Write("flows/probe.yaml", Probe("probe", "datasets:", Dataset("writes", "wks:well:1.0.0"), Dataset("writes", " WKS:Well:1.0.0 ")));

        var collected = Collect();

        Assert.Single(collected.Facts, f => f.Flow == "probe" && f.Relation == LineageRelation.Writes);
    }

    [Theory]
    [InlineData("relation: writes, system: probe-store, namespace: t, group: g, name: \"wks:*\"", "writes 'wks:*', a wildcard; a flow writes named datasets only")]
    [InlineData("relation: reads, system: \"Probe Store\", namespace: t, group: g, name: x", "names the system 'Probe Store'; a system is lower-case letters")]
    [InlineData("relation: reads, system: \"9store\", namespace: t, group: g, name: x", "names the system '9store'")]
    [InlineData("relation: reads, system: probe-store, namespace: \" \", group: g, name: x", "names no namespace")]
    [InlineData("relation: reads, system: probe-store, namespace: t, group: \"\", name: x", "names no group")]
    [InlineData("relation: reads, system: probe-store, namespace: t, group: g, name: \"a|b\"", "has a name carrying a '|' or a control character")]
    [InlineData("relation: reads, system: probe-store, namespace: t, group: g, name: x, separator: \"*\"", "has a segment separator that is a wildcard")]
    [InlineData("relation: requires, system: probe-store, namespace: t, group: g, name: x", "has the relation 'Requires'")]
    public void An_unusable_dataset_declaration_skips_the_document(string declaration, string expected)
    {
        Write("flows/probe.yaml", Probe("probe", "datasets:", "  - { " + declaration + " }"));

        var warning = SkippedWarning(Collect());

        Assert.Contains($"probe flow 'probe' declared dataset 1 {expected}", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_dataset_name_skips_the_document()
    {
        Write("flows/probe.yaml", Probe("probe", "datasets:", Dataset("reads", new string('x', DeclaredDataset.MaxPartLength + 1))));

        var warning = SkippedWarning(Collect());

        Assert.Contains($"has a name longer than {DeclaredDataset.MaxPartLength} characters", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_instance_is_identified_by_hash_and_never_echoed()
    {
        const string literal = "https://store.example.com/;Password=hunter2";
        Write("flows/probe.yaml", Probe("probe", "datasets:", Dataset("writes", "wks:well:1.0.0", instance: literal)));
        Write("flows/refused.yaml", Probe("refused", "datasets:", Dataset("writes", "wks:*", instance: literal)));

        var collected = Collect();

        var fact = Assert.Single(collected.Facts, f => f.Flow == "probe");
        Assert.StartsWith("dataset:probe-store:", fact.ServerRef, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", fact.ServerRef, StringComparison.Ordinal);
        Assert.DoesNotContain("store.example.com", fact.ServerRef, StringComparison.Ordinal);
        Assert.Contains(collected.Warnings, w => w.Contains("flows/refused.yaml: skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("hunter2", StringComparison.Ordinal) || w.Contains("store.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void A_description_that_throws_skips_the_document_with_a_redacted_reason()
    {
        Write("flows/probe.yaml", Probe("probe", "lineageFailure: \"the store refused Password=hunter2\""));

        var warning = SkippedWarning(Collect());

        Assert.Contains("probe flow 'probe' could not describe its lineage (InvalidOperationException)", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_warnings_are_reported_against_the_document_and_its_declarations_still_count()
    {
        Write("flows/probe.yaml", Probe(
            "probe",
            "lineageWarnings: [\"the mapping 'm.yaml' was not found.\", \"  \"]",
            "datasets:",
            Dataset("writes", "wks:well:1.0.0")));

        var collected = Collect();

        Assert.Single(collected.Facts, f => f.Flow == "probe");
        Assert.Contains("flows/probe.yaml: the mapping 'm.yaml' was not found.", collected.Warnings);
        Assert.DoesNotContain(collected.Warnings, w => w.EndsWith("flows/probe.yaml: ", StringComparison.Ordinal));
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_description_reads_its_companions_from_the_estate_only()
    {
        Write("flows/names.txt", "wks:well:1.0.0\n\nwks:log:1.0.0\n");
        Write("flows/probe.yaml", Probe("probe", "companion: names.txt"));
        Write("flows/outside.yaml", Probe("outside", "companion: ../../outside-the-estate.txt"));

        var collected = Collect();

        Assert.Equal(
            ["wks:log:1.0.0", "wks:well:1.0.0"],
            collected.Facts.Where(f => f.Flow == "probe").Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Contains(
            "flows/outside.yaml: the companion '../../outside-the-estate.txt' lies outside the estate and was not read.",
            collected.Warnings);
    }
}
