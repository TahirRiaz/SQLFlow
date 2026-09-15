using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The lineage contributor of a registered flow kind (<see cref="RegisteredFlowDocument.DeclaredObjects"/>): the estate
/// scan turns what a registered flow declares it reads and writes into declared facts on the same node identities the
/// built-in kinds use, so the execution plan orders a pre flow, the ing flow that merges its output, a registered flow
/// that reads the merge's target, and an ing flow that reads what the registered flow writes, in that order. The kind is
/// the test-only <see cref="ProbeFlowKind"/>.
/// </summary>
public sealed class LineageRegisteredKindTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    private const string Pre = "${env:SQLFLOW_CONN_PRE}";

    private const string Ods = "${env:SQLFLOW_CONN_ODS}";

    private const string Dw = "${env:SQLFLOW_CONN_DW}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-kind-lineage-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageRegisteredKindTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static YamlDocumentLoader Loader() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind()]);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string PreFlow = """
        name: wells_01_pre
        source:
          type: json
          location: https://acct.dfs.core.windows.net/datalakev2/raw/wells/
          options: { srcFile: "*.json" }
        target:
          connection: ${env:SQLFLOW_CONN_PRE}
          schema: pre
          table: Wells
        transform:
          generateView: true
          columns:
            - { name: id, expr: "CAST(@ColName as varchar(50))" }
        """;

    private const string IngFlow = """
        flowType: ing
        name: wells_02_ing
        connections:
          pre: ${env:SQLFLOW_CONN_PRE}
          ods: ${env:SQLFLOW_CONN_ODS}
        source:
          server: pre
          object: "[PreDb].[pre].[v_Wells]"
        target:
          server: ods
          object: "[OdsDb].[arc].[Wells]"
        load:
          keyColumns: [id]
        """;

    private const string RegisteredFlow = """
        flowType: probe
        name: wells_03_probe
        source: s3://drops/wells/
        reads:
          - connection: "${env:SQLFLOW_CONN_ODS}"
            object: "[OdsDb].[arc].[Wells]"
        writes:
          - connection: "${env:SQLFLOW_CONN_ODS}"
            object: "[OdsDb].[arc].[WellStatus]"
        """;

    private const string DownstreamIngFlow = """
        flowType: ing
        name: wells_04_ing
        connections:
          ods: ${env:SQLFLOW_CONN_ODS}
          dw: ${env:SQLFLOW_CONN_DW}
        source:
          server: ods
          object: "[OdsDb].[arc].[WellStatus]"
        target:
          server: dw
          object: "[DW].[dbo].[WellStatus]"
        load:
          keyColumns: [id]
        """;

    [Fact]
    public void Waves_OrderPreIngRegisteredAndDownstreamIng_ByTheirDeclaredReadsAndWrites()
    {
        Write("wells/01_pre.yaml", PreFlow);
        Write("wells/02_ing.yaml", IngFlow);
        Write("wells/03_probe.yaml", RegisteredFlow);
        Write("wells/04_ing.yaml", DownstreamIngFlow);

        var collected = new FlowSetCollector(Loader()).Collect(_root);
        var report = LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);

        Assert.Equal(
            [["wells_01_pre"], ["wells_02_ing"], ["wells_03_probe"], ["wells_04_ing"]],
            report.ExecutionPlan.Waves.Select(w => w.Flows.ToList()).ToList());
        Assert.Empty(report.ExecutionPlan.Unordered);
    }

    [Fact]
    public void Waves_WithoutDeclarations_LeaveTheRegisteredFlowUnordered()
    {
        // The same estate with a registered flow that declares nothing: the flow relates to no object, so nothing orders
        // it after the merge, which is exactly what the declarations above add.
        Write("wells/01_pre.yaml", PreFlow);
        Write("wells/02_ing.yaml", IngFlow);
        Write("wells/03_probe.yaml", "flowType: probe\nname: wells_03_probe\nsource: s3://drops/wells/\n");

        var collected = new FlowSetCollector(Loader()).Collect(_root);
        var report = LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);

        Assert.DoesNotContain(report.ExecutionPlan.Waves, w => w.Flows.Contains("wells_03_probe") && w.Wave > 1);
        Assert.DoesNotContain(collected.Facts, f => f.Flow == "wells_03_probe");
    }

    [Fact]
    public void Collector_TurnsDeclarationsIntoDeclaredFacts_AndRegistersTheirServers()
    {
        Write("wells/03_probe.yaml", RegisteredFlow);

        var collected = new FlowSetCollector(Loader()).Collect(_root);

        var facts = collected.Facts.Where(f => f.Flow == "wells_03_probe").ToList();
        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, f => f.Relation == LineageRelation.Reads && f.ServerRef == ServerIdentity.From(Ods)
            && f.Database == "OdsDb" && f.Schema == "arc" && f.Name == "Wells"
            && f.Tier == LineageTier.Declared && f.KindHint == LineageNodeKind.Unknown);
        Assert.Contains(facts, f => f.Relation == LineageRelation.Writes && f.Name == "WellStatus"
            && f.KindHint == LineageNodeKind.Table);
        Assert.Equal((Ods, Core.Connections.DataSourceKind.MSSQL), collected.Servers[ServerIdentity.From(Ods)]);
    }

    [Fact]
    public void Collector_ARepeatedDeclaration_IsOneFact()
    {
        // The same object, bracketed and bare: one node, so one fact.
        Write("wells/03_probe.yaml", """
            flowType: probe
            name: wells_03_probe
            source: s3://drops/wells/
            reads:
              - connection: "${env:SQLFLOW_CONN_ODS}"
                object: "[OdsDb].[arc].[Wells]"
              - connection: "${env:SQLFLOW_CONN_ODS}"
                object: "OdsDb.arc.Wells"
            """);

        var collected = new FlowSetCollector(Loader()).Collect(_root);

        Assert.Single(collected.Facts, f => f.Flow == "wells_03_probe" && f.Relation == LineageRelation.Reads);
    }

    [Fact]
    public void Collector_ADeclarationWithoutAConnection_SkipsTheDocument_NamingTheFlowAndPosition()
    {
        Write("wells/03_probe.yaml", """
            flowType: probe
            name: wells_03_probe
            source: s3://drops/wells/
            reads:
              - object: "[OdsDb].[arc].[Wells]"
            """);

        var collected = new FlowSetCollector(Loader()).Collect(_root);

        Assert.Empty(collected.Flows);
        Assert.Empty(collected.Facts);
        var warning = Assert.Single(collected.Warnings);
        Assert.Contains("wells/03_probe.yaml: skipped", warning, StringComparison.Ordinal);
        Assert.Contains("probe flow 'wells_03_probe' declared object 1 names no connection reference", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Collector_ARelationOtherThanReadsOrWrites_SkipsTheDocument()
    {
        Write("wells/03_probe.yaml", """
            flowType: probe
            name: wells_03_probe
            source: s3://drops/wells/
            requires:
              - connection: "${env:SQLFLOW_CONN_ODS}"
                object: "[OdsDb].[dbo].[usp_Refresh]"
            """);

        var collected = new FlowSetCollector(Loader()).Collect(_root);

        Assert.Empty(collected.Flows);
        Assert.Contains(collected.Warnings,
            w => w.Contains("has the relation 'Requires'; a flow declares only reads and writes", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_ALiteralConnection_IsNeverEchoedByTheRefusal()
    {
        Write("wells/03_probe.yaml", """
            flowType: probe
            name: wells_03_probe
            source: s3://drops/wells/
            requires:
              - connection: "Server=db;Password=hunter2"
                object: "[OdsDb].[dbo].[usp_Refresh]"
            """);

        var collected = new FlowSetCollector(Loader()).Collect(_root);

        Assert.DoesNotContain(collected.Warnings, w => w.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void QualifiedName_TakesTheRightmostThreeParts_AndRefusesAShortName()
    {
        var read = DeclaredDataObject.Reads(Ods, "[srv].[OdsDb].[arc].[Well]]s]");
        Assert.Equal(LineageRelation.Reads, read.Relation);
        Assert.Equal(("OdsDb", "arc", "Well]s"), (read.Database, read.Schema, read.Name));
        Assert.Null(read.Kind);

        Assert.Equal(LineageRelation.Writes, DeclaredDataObject.Writes(Dw, "DW.dbo.WellStatus").Relation);
        Assert.Throws<SqlFlowException>(() => DeclaredDataObject.Reads(Ods, "arc.Wells"));
    }
}
