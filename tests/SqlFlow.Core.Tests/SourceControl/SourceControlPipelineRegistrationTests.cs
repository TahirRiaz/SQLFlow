using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// How a source-control flow registers with the rest of the system: it IS a pipeline (so it holds a schedule,
/// gets fired by the scheduler, and keeps run history like any other flow) but it is NOT part of the lineage
/// graph (it reads object definitions and writes a git tree, so it moves no data and must never order the
/// estate). These two facts are in tension, so they are pinned together here.
/// </summary>
public sealed class SourceControlPipelineRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-scm-reg-" + Guid.NewGuid().ToString("N")[..8]);

    public SourceControlPipelineRegistrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string SnapshotYaml = """
        flowType: scm
        name: warehouse-scm
        batch: schema-history
        connections:
          dwh: ${env:SQLFLOW_CONN_DWH}
        source:
          server: dwh
          database: DW
        repository:
          path: /var/sqlflow/scm/warehouse
        schedule:
          cron: "0 3 * * *"
          timezone: Europe/Oslo
        """;

    private const string IngestionYaml = """
        flowType: ing
        name: load-orders
        connections:
          src: ${env:SQLFLOW_CONN_SRC}
          dwh: ${env:SQLFLOW_CONN_DWH}
        source: { server: src, object: Staging.dbo.Orders }
        target: { server: dwh, object: DW.dbo.Orders }
        load: { keyColumns: [OrderID] }
        """;

    [Fact]
    public void Header_ProjectsARunnableFlow_MarkedOutOfLineage()
    {
        var document = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
            new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(),
            new YamlTranslateFlowLoader()).Parse(SnapshotYaml);

        var header = Assert.Single(FlowDocumentHeaders.Project(document));

        Assert.Equal("warehouse-scm", header.Name);
        Assert.Equal("scm", header.Kind);
        Assert.Equal("schema-history", header.Batch);

        // It reads the declared SQL Server and writes a git tree on the file system.
        Assert.Equal("${env:SQLFLOW_CONN_DWH}", header.SourceServerRef);
        Assert.Equal(ServerIdentity.FileSystem, header.TargetServerRef);

        // The schedule rides along, which is what makes the snapshot fire on the existing scheduler.
        Assert.NotNull(header.Schedule);
        Assert.Equal("0 3 * * *", header.Schedule!.Cron);

        Assert.False(header.ParticipatesInLineage);
    }

    [Fact]
    public void Collector_KeepsTheFlow_ButContributesNoFacts()
    {
        Write("warehouse-scm.flow.yaml", SnapshotYaml);
        Write("load-orders.flow.yaml", IngestionYaml);

        var collected = new FlowSetCollector().Collect(_root);

        var snapshot = Assert.Single(collected.Flows, f => f.Node.Name == "warehouse-scm");
        Assert.Equal("scm", snapshot.Node.Kind);
        Assert.False(snapshot.ParticipatesInLineage);
        Assert.True(collected.Flows.Single(f => f.Node.Name == "load-orders").ParticipatesInLineage);

        // No relation is claimed for it: scripting a definition is not reading data.
        Assert.DoesNotContain(collected.Facts, f => f.Flow == "warehouse-scm");
    }

    [Fact]
    public void Collector_RegistersItsSchedule_SoItCanFire()
    {
        Write("warehouse-scm.flow.yaml", SnapshotYaml);

        var collected = new FlowSetCollector().Collect(_root);

        var schedule = Assert.Single(collected.Schedules);
        Assert.Equal("warehouse-scm", schedule.Name);
        Assert.Equal("0 3 * * *", schedule.Spec.Cron);
        Assert.Equal("Europe/Oslo", schedule.Spec.Timezone);
        Assert.Equal(["warehouse-scm"], schedule.Members);
    }

    [Fact]
    public async Task Graph_ExcludesTheFlow_FromNodesAndWaves()
    {
        using var harness = new LineageEstateHarness();
        harness.Flow("warehouse-scm.flow.yaml", SnapshotYaml);
        harness.Flow("load-orders.flow.yaml", IngestionYaml);

        var report = await harness.ComputeAsync(includeObserved: false);

        Assert.Contains(report.Flows, f => f.Name == "load-orders");
        Assert.DoesNotContain(report.Flows, f => f.Name == "warehouse-scm");
        Assert.DoesNotContain(report.Edges, e => e.Flow == "warehouse-scm");
        Assert.DoesNotContain(report.FlowDependencies, d => d.FromFlow == "warehouse-scm" || d.ToFlow == "warehouse-scm");
        Assert.DoesNotContain(
            LineageEstateHarness.Waves(report).SelectMany(w => w), flow => flow == "warehouse-scm");
    }
}
