using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The tiered collection: a flow estate on disk scans into declared facts per document kind (with hooks
/// AST-extracted and broken files skipped loudly), the canonical run artifacts replay into observed facts
/// (transient staging dissolved, source steps attributed to the source server, staleness detected, corrupt
/// artifacts tolerated), and the identity rules (server references, node keys) hold everywhere.
/// </summary>
public sealed class LineageCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-lineage-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageCollectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string IngestionYaml = """
        flowType: ing
        name: load-orders
        connections:
          src: ${env:SQLFLOW_CONN_SRC}
          dwh: ${env:SQLFLOW_CONN_DWH}
        source: { server: src, object: Staging.dbo.Orders }
        target: { server: dwh, object: DW.dbo.Orders }
        load:
          keyColumns: [OrderID]
        postProcess: "EXEC DW.audit.usp_Stamp; INSERT INTO DW.audit.LoadLog (At) SELECT GETUTCDATE();"
        """;

    [Fact]
    public void FlowSet_CollectsEveryKind_WithDeclaredEndpoints()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        Write("orders-watch.flow.yaml", """
            flowType: hc
            name: orders-watch
            connections:
              dwh: ${env:SQLFLOW_CONN_DWH}
            target: { server: dwh, object: DW.dbo.Orders }
            dateColumn: OrderDate
            baseValue: COUNT(*)
            """);
        Write("refresh.flow.yaml", """
            flowType: sp
            name: refresh-marts
            connections:
              dwh: ${env:SQLFLOW_CONN_DWH}
            procedure: { server: dwh, object: DW.dbo.usp_Refresh }
            """);

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Equal(["load-orders", "orders-watch", "refresh-marts"],
            collected.Flows.Select(f => f.Node.Name).OrderBy(n => n, StringComparer.Ordinal));

        // ing: read source, write target, plus the post-process hook's facts via the AST.
        var ingestion = collected.Facts.Where(f => f.Flow == "load-orders").ToList();
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Reads && f.Name == "Orders" && f.Database == "Staging"
            && f.ServerRef == "${env:SQLFLOW_CONN_SRC}");
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Writes && f.Name == "Orders" && f.Database == "DW");
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Requires && f.Name == "usp_Stamp" && f.Schema == "audit");
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Writes && f.Name == "LoadLog");

        // hc reads its target; sp requires its procedure.
        Assert.Contains(collected.Facts, f => f.Flow == "orders-watch" && f.Relation == LineageRelation.Reads && f.Name == "Orders");
        Assert.Contains(collected.Facts, f => f.Flow == "refresh-marts" && f.Relation == LineageRelation.Requires && f.Name == "usp_Refresh");

        // The server inventory carries both references for the derived tier.
        Assert.Contains("${env:SQLFLOW_CONN_SRC}", collected.Servers.Keys);
        Assert.Contains("${env:SQLFLOW_CONN_DWH}", collected.Servers.Keys);
    }

    [Fact]
    public void FlowSet_UnparseableYaml_IgnoredSilently()
    {
        // Extension-based discovery: a .yaml that does not parse as a flow is a non-flow file (a library, config, or
        // unrelated yaml), so it is ignored rather than reported as broken; the valid flows still collect.
        Write("good.flow.yaml", IngestionYaml);
        Write("not-a-flow.yaml", "flowType: ing\nname: [not, a, name");

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Single(collected.Flows);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("not-a-flow.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowSet_PlainYamlExtension_IsDiscoveredAsFlow()
    {
        // A flow document is discovered by its .yaml extension; the historical .flow.yaml suffix is no longer required.
        Write("orders.01_pre.yaml", IngestionYaml);

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Single(collected.Flows);
        Assert.Equal("load-orders", collected.Flows[0].Node.Name);
    }

    [Fact]
    public void FlowSet_DuplicateFlowNames_Warn()
    {
        Write("a/one.flow.yaml", IngestionYaml);
        Write("b/two.flow.yaml", IngestionYaml);

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Contains(collected.Warnings, w => w.Contains("'load-orders' is declared by 2 documents", StringComparison.Ordinal));
    }

    private static string IngestionFlow(string name, string scheduleLine) => $$"""
        flowType: ing
        name: {{name}}
        connections:
          src: ${env:SQLFLOW_CONN_SRC}
          dwh: ${env:SQLFLOW_CONN_DWH}
        source: { server: src, object: Staging.dbo.{{name}}Src }
        target: { server: dwh, object: DW.dbo.{{name}}Tgt }
        load:
          keyColumns: [Id]
        {{scheduleLine}}
        """;

    [Fact]
    public void Schedules_ReferenceToDedicatedLibrary_BecomesOneScheduleWithBothMembers()
    {
        Write("schedules.yaml", """
            schedules:
              nightly: { cron: "0 6 * * *", timezone: "Europe/Oslo" }
            """);
        Write("a.flow.yaml", IngestionFlow("alpha", "schedule: nightly"));
        Write("b.flow.yaml", IngestionFlow("beta", "schedule: nightly"));

        var collected = new FlowSetCollector().Collect(_root);

        // ONE schedule with two members, not two schedules with a copied cadence: the whole point of the model, and
        // what makes the fire a single wave-ordered group instead of a race.
        var nightly = Assert.Single(collected.Schedules);
        Assert.Equal("nightly", nightly.Name);
        Assert.Equal("0 6 * * *", nightly.Spec.Cron);
        Assert.Equal("Europe/Oslo", nightly.Spec.Timezone);
        Assert.Equal(["alpha", "beta"], nightly.Members.OrderBy(m => m, StringComparer.Ordinal));

        // The referencing flows carry the membership, never a cadence of their own.
        foreach (var name in new[] { "alpha", "beta" })
        {
            var flow = collected.Flows.Single(f => f.Node.Name == name);
            Assert.True(flow.Schedule!.IsReference);
            Assert.Null(flow.Schedule.Cron);
        }

        // The library file is not itself a flow.
        Assert.DoesNotContain(collected.Flows, f => f.Node.Name == "nightly");
    }

    [Fact]
    public void Schedules_NamedInlineBlock_TakesTheDeclaringFlowAndEveryJoinerAsMembers()
    {
        Write("publisher.flow.yaml", IngestionFlow("publisher", """
            schedule:
              name: nightly
              cron: "0 6 * * *"
              timezone: "Europe/Oslo"
            """));
        Write("consumer.flow.yaml", IngestionFlow("consumer", "schedule: nightly"));

        var collected = new FlowSetCollector().Collect(_root);

        // Declaring the cadence inline schedules the declaring flow too, so publisher and consumer are members of
        // the one schedule and one fire runs both.
        var nightly = Assert.Single(collected.Schedules);
        Assert.Equal("nightly", nightly.Name);
        Assert.Equal("0 6 * * *", nightly.Spec.Cron);
        Assert.Equal(["consumer", "publisher"], nightly.Members.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void Schedules_UnnamedInlineBlock_IsNamedAfterItsFlowAndHasThatOneMember()
    {
        Write("solo.flow.yaml", IngestionFlow("solo", """
            schedule:
              cron: "0 6 * * *"
            """));

        var collected = new FlowSetCollector().Collect(_root);

        // Every schedule is named, so a plain inline block still resolves to a member set a fire can run.
        var schedule = Assert.Single(collected.Schedules);
        Assert.Equal("solo", schedule.Name);
        Assert.Equal(["solo"], schedule.Members);
    }

    [Fact]
    public void Schedules_CarryWhereTheyAreDeclared_SoTheCatalogCanServeTheDefiningYaml()
    {
        Write("schedules.yaml", """
            schedules:
              nightly: { cron: "0 4 * * *" }
            """);
        Write("joiner.flow.yaml", IngestionFlow("joiner", "schedule: nightly"));
        Write("solo.flow.yaml", IngestionFlow("solo", """
            schedule:
              cron: "0 6 * * *"
            """));

        var collected = new FlowSetCollector().Collect(_root);

        // A library entry points at the file and carries its text: nothing else in the catalog holds a schedules.yaml,
        // so without this "show me the YAML behind this schedule" would have no answer for it.
        var nightly = collected.Schedules.Single(s => s.Name == "nightly");
        Assert.Equal("schedules.yaml", nightly.OriginFile);
        Assert.Null(nightly.OriginFlow);
        Assert.Contains("nightly: { cron: \"0 4 * * *\" }", nightly.LibraryYaml, StringComparison.Ordinal);

        // An inline block points at its declaring flow and carries no text: that document is already stored, redacted,
        // on the pipeline row, and a second copy here would be an unredacted one.
        var solo = collected.Schedules.Single(s => s.Name == "solo");
        Assert.Equal("solo.flow.yaml", solo.OriginFile);
        Assert.Equal("solo", solo.OriginFlow);
        Assert.Null(solo.LibraryYaml);
    }

    [Fact]
    public void Schedules_SequenceReference_JoinsTheFlowToEverySchedule()
    {
        Write("schedules.yaml", """
            schedules:
              dwh_nightly:      { cron: "0 4 * * *" }
              dwh_small_hourly: { cron: "0 * * * *" }
            """);
        Write("small.flow.yaml", IngestionFlow("dim_currency", "schedule: [dwh_nightly, dwh_small_hourly]"));
        Write("big.flow.yaml", IngestionFlow("fact_sales", "schedule: dwh_nightly"));

        var collected = new FlowSetCollector().Collect(_root);

        var nightly = collected.Schedules.Single(s => s.Name == "dwh_nightly");
        Assert.Equal(["dim_currency", "fact_sales"], nightly.Members.OrderBy(m => m, StringComparer.Ordinal));

        var hourly = collected.Schedules.Single(s => s.Name == "dwh_small_hourly");
        Assert.Equal(["dim_currency"], hourly.Members);
    }

    [Fact]
    public void Schedules_UnknownReference_WarnsAndLeavesTheFlowUnscheduled()
    {
        Write("orphan.flow.yaml", IngestionFlow("orphan", "schedule: ghost"));

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Empty(collected.Schedules);
        Assert.Contains(collected.Warnings, w => w.Contains("joins schedule 'ghost'", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedules_LibraryEntryNobodyJoins_Warns()
    {
        // A schedule with no members fires nothing. That is a renamed source or a typo on the joining side, not an
        // intent, so it is surfaced rather than sitting in the catalog looking armed.
        Write("schedules.yaml", """
            schedules:
              nightly: { cron: "0 6 * * *" }
            """);

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Contains(collected.Warnings, w => w.Contains("has no members", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedules_FlowAttachedToNothing_Warns()
    {
        // The check that makes "every pipeline must be attached to a schedule" enforceable: a flow nothing schedules
        // will never run, and until membership was explicit that was indistinguishable from a flow swept up by a label.
        Write("stray.flow.yaml", IngestionFlow("stray", string.Empty));

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Contains(collected.Warnings, w =>
            w.Contains("'stray'", StringComparison.Ordinal)
            && w.Contains("attached to no schedule", StringComparison.Ordinal));
    }

    private void WriteRunArtifact(string flowName, string resultJson, DateTime writtenUtc)
    {
        var safe = Core.Runs.RunHistoryWriter.SafeName(flowName);
        var folder = Path.Combine(_root, ".sqlflow", "runs", safe, $"{writtenUtc:yyyyMMdd-HHmmss}_abcd1234");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "7e4f4d3b-1111-2222-3333-444455556666",
              "success": true,
              "writtenUtc": "{{writtenUtc:yyyy-MM-ddTHH:mm:ssZ}}",
              "result": {{resultJson}}
            }
            """);
    }

    [Fact]
    public void RunArtifacts_ReplayTheTrace_StagingDissolves_SidesAttribute()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        WriteRunArtifact("load-orders", """
            {
              "sqlTrace": [
                { "sequence": 1, "step": "source.select", "sql": "SELECT * FROM [Staging].[dbo].[Orders] WHERE Id > 5;" },
                { "sequence": 2, "step": "staging.create", "sql": "CREATE TABLE [DW].[dbo].[stg_orders_123] (Id int);" },
                { "sequence": 3, "step": "upsert.insert", "sql": "INSERT INTO [DW].[dbo].[Orders] SELECT * FROM [DW].[dbo].[stg_orders_123];" },
                { "sequence": 4, "step": "staging.drop", "sql": "DROP TABLE [DW].[dbo].[stg_orders_123];" },
                { "sequence": 5, "step": "postprocess", "sql": "INSERT INTO [DW].[audit].[LoadLog] (At) SELECT GETUTCDATE();" }
              ]
            }
            """, new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc));

        // The document predates the run: no staleness.
        File.SetLastWriteTimeUtc(Path.Combine(_root, "load-orders.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        // Source reads attribute to the source server; target work to the target server.
        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Reads && f.Database == "Staging"
            && f.ServerRef == "${env:SQLFLOW_CONN_SRC}" && f.Tier == LineageTier.Observed && f.RunId is not null);
        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Writes && f.Name == "Orders" && f.Database == "DW"
            && f.ServerRef == "${env:SQLFLOW_CONN_DWH}");
        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Writes && f.Name == "LoadLog");

        // The transient staging table dissolved.
        Assert.DoesNotContain(observed.Facts, f => f.Name.Contains("stg_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observed.Warnings, w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunArtifacts_CreatedView_AttributesBodyReadsToTheViewModule()
    {
        // The run built a transform view; the observed tier must attribute what the view's SELECT reads to the
        // view AS A MODULE (flow-less, viaModule = the view), re-extracted from the actually-executed DDL, so the
        // graph draws the view to its parent table with observed provenance and no live catalog.
        // The engine's TransformViewBuilder emits a two-part view/table name (CREATE VIEW cannot database-qualify
        // its own name), so the trace carries exactly that; the database is completed later by identity unification.
        Write("load-orders.flow.yaml", IngestionYaml);
        WriteRunArtifact("load-orders", """
            {
              "sqlTrace": [
                { "sequence": 1, "step": "transform.view", "sql": "CREATE OR ALTER VIEW [dbo].[v_Orders] AS SELECT [Id], CAST([Total] AS decimal(9,2)) AS [Total] FROM [dbo].[Orders];" }
              ]
            }
            """, new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "load-orders.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        var viewKey = NodeKey.For("${env:SQLFLOW_CONN_DWH}", null, "dbo", "v_Orders");
        var tableKey = NodeKey.For("${env:SQLFLOW_CONN_DWH}", null, "dbo", "Orders");

        // The module edge is flow-less, observed tier, carries the run id, and reads the parent table.
        Assert.Contains(observed.Facts, f =>
            f.Flow is null && f.ViaModuleKey == viewKey && f.Relation == LineageRelation.Reads
            && NodeKey.For(f.ServerRef, f.Database, f.Schema, f.Name) == tableKey
            && f.Tier == LineageTier.Observed && f.RunId is not null);

        // The view reading itself is a self-fact and is filtered.
        Assert.DoesNotContain(observed.Facts, f =>
            f.ViaModuleKey == viewKey && NodeKey.For(f.ServerRef, f.Database, f.Schema, f.Name) == viewKey);
    }

    [Fact]
    public void RunArtifacts_CreatedThenDroppedView_ContributesNoModuleFacts()
    {
        // A view the same run created and then dropped is transient: no module edge should survive, matching the
        // main extraction's created-then-dropped hygiene.
        Write("load-orders.flow.yaml", IngestionYaml);
        WriteRunArtifact("load-orders", """
            {
              "sqlTrace": [
                { "sequence": 1, "step": "transform.view", "sql": "CREATE OR ALTER VIEW [dbo].[v_Tmp] AS SELECT [Id] FROM [dbo].[Orders];" },
                { "sequence": 2, "step": "transform.drop", "sql": "DROP VIEW [dbo].[v_Tmp];" }
              ]
            }
            """, new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "load-orders.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        var viewKey = NodeKey.For("${env:SQLFLOW_CONN_DWH}", null, "dbo", "v_Tmp");
        Assert.DoesNotContain(observed.Facts, f => f.ViaModuleKey == viewKey);
    }

    [Fact]
    public void RunArtifacts_DocumentNewerThanRun_WarnsStale()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        WriteRunArtifact("load-orders", """{ "sqlTrace": [] }""", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "load-orders.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Warnings, w => w.Contains("changed after its last run", StringComparison.Ordinal));
    }

    [Fact]
    public void RunArtifacts_CorruptJson_WarnsAndContinues()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        var folder = Path.Combine(_root, ".sqlflow", "runs", "load-orders", "20260611-060000_abcd1234");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), "{ not json");

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Warnings, w => w.Contains("run artifact unreadable", StringComparison.Ordinal));
        Assert.Empty(observed.Facts);
    }

    [Fact]
    public void RunArtifacts_UnmatchedRunFolder_Warns()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        Directory.CreateDirectory(Path.Combine(_root, ".sqlflow", "runs", "deleted-flow"));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Warnings, w => w.Contains("'deleted-flow' has no matching flow document", StringComparison.Ordinal));
    }

    [Fact]
    public void RunArtifacts_FileFlowDdl_IsObservedToo()
    {
        Write("csv-load.flow.yaml", """
            name: csv-load
            source: { type: csv, location: ./data/orders.csv }
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: raw
              table: Orders
            """);
        WriteRunArtifact("csv-load", """
            { "ddlExecuted": [ "CREATE TABLE [raw].[Orders] ([Id] int NULL);" ] }
            """, new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "csv-load.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Creates && f.Name == "Orders" && f.Schema == "raw");
    }

    private const string InvokeYaml = """
        flowType: inv
        name: fetch-orders
        servicePrincipals:
          deploy: { subscriptionId: s, resourceGroup: rg, dataFactoryName: adf }
        invoke:
          type: adf
          pipeline: pl_fetch_orders
          servicePrincipal: deploy
          output:
            location: ./data/incoming
            srcFile: orders_*.csv
        """;

    private const string CsvIngestionYaml = """
        name: load-incoming-orders
        source:
          type: csv
          location: ./data/incoming
          options: { srcFile: "orders_*.csv" }
        target:
          connection: ${env:SQLFLOW_CONN_DWH}
          schema: raw
          table: Orders
        """;

    [Fact]
    public void Invoke_Output_LinksToFileIngestion_ViaSharedFileNode()
    {
        Write("fetch-orders.flow.yaml", InvokeYaml);
        Write("load-orders.flow.yaml", CsvIngestionYaml);

        var collected = new FlowSetCollector().Collect(_root);

        // The ingestion reads the folder's file node...
        var read = Assert.Single(collected.Facts, f =>
            f.Flow == "load-incoming-orders" && f.Relation == LineageRelation.Reads
            && f.KindHint == LineageNodeKind.File);
        // ...and the invoke is attributed a Writes of the SAME node, so the graph chains invoke -> file -> table.
        Assert.Contains(collected.Facts, f =>
            f.Flow == "fetch-orders" && f.Relation == LineageRelation.Writes
            && f.KindHint == LineageNodeKind.File && f.ServerRef == ServerIdentity.FileSystem
            && f.Name == read.Name);
        Assert.Equal("data/incoming", read.Name);
    }

    [Fact]
    public void Invoke_Output_NoConsumer_RecordsItsOwnDeclaredNode()
    {
        Write("fetch-orders.flow.yaml", InvokeYaml);

        var collected = new FlowSetCollector().Collect(_root);

        // Nothing consumes it yet, but the declared output is still a visible file node the invoke writes.
        Assert.Contains(collected.Facts, f =>
            f.Flow == "fetch-orders" && f.Relation == LineageRelation.Writes
            && f.KindHint == LineageNodeKind.File && f.Name == "data/incoming");
    }

    [Fact]
    public void Invoke_Output_FolderMismatch_DoesNotLink()
    {
        Write("fetch-orders.flow.yaml", InvokeYaml.Replace("location: ./data/incoming", "location: ./data/invoices", StringComparison.Ordinal));
        Write("load-orders.flow.yaml", CsvIngestionYaml);

        var collected = new FlowSetCollector().Collect(_root);

        // The invoke lands in a different folder, so it writes its own node, never the ingestion's.
        Assert.Contains(collected.Facts, f =>
            f.Flow == "fetch-orders" && f.Relation == LineageRelation.Writes && f.Name == "data/invoices");
        Assert.DoesNotContain(collected.Facts, f =>
            f.Flow == "fetch-orders" && f.Name == "data/incoming");
    }

    [Fact]
    public void ServerIdentity_ReferencesPassThrough_InlineLiteralsHash()
    {
        Assert.Equal("${env:SQLFLOW_CONN_DWH}", ServerIdentity.From(" ${env:SQLFLOW_CONN_DWH} "));
        Assert.Equal("@warehouse", ServerIdentity.From("@warehouse"));

        var first = ServerIdentity.From("Server=db1;Database=DW;User ID=u;Password=hunter2");
        var second = ServerIdentity.From("server=DB1;database=dw;user id=U;password=HUNTER2");
        Assert.StartsWith("inline:", first, StringComparison.Ordinal);

        // Equal up to case: same identity; the literal (and its secret) never appears.
        Assert.Equal(first, second);
        Assert.DoesNotContain("hunter", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db1", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodeKey_CaseFolds_AndKeepsAbsentPartsDistinct()
    {
        Assert.Equal(NodeKey.For("@DWH", "Dw", "DBO", "Orders"), NodeKey.For("@dwh", "dw", "dbo", "orders"));
        Assert.NotEqual(NodeKey.For("@dwh", null, "dbo", "orders"), NodeKey.For("@dwh", "dw", "dbo", "orders"));
        Assert.NotEqual(NodeKey.For("@a", "dw", "dbo", "x"), NodeKey.For("@b", "dw", "dbo", "x"));
    }
}
