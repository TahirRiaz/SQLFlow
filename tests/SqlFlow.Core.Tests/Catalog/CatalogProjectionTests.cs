using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Core.Lineage;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The pure projections from the git/disk source of truth into shadow entities: the run.json envelope mapped to a
/// run row (header + best-effort metrics across flow kinds, with a stable pipeline link), and the pipeline
/// builder. No EF, no database - just the mapping rules the sync relies on.
/// </summary>
public sealed class CatalogProjectionTests
{
    private static readonly Guid Repo = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void RunFromJson_File_MapsHeaderAndMillisecondDuration()
    {
        var run = CatalogProjection.RunFromJson(Json("""
            {
              "schemaVersion": 1,
              "flowKind": "file",
              "flowName": "orders",
              "runId": "11111111-1111-1111-1111-111111111111",
              "success": true,
              "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "rowsLoaded": 42, "totalMs": 1500.0 }
            }
            """), Repo);

        Assert.NotNull(run);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), run!.RunId);
        Assert.Equal("orders", run.FlowName);
        Assert.Equal("file", run.FlowKind);
        Assert.Equal(Repo, run.RepoId);
        Assert.Equal(CatalogIdentity.Pipeline(Repo, "orders"), run.PipelineId); // the repo-scoped join key to the pipeline
        Assert.True(run.Success);
        Assert.Equal(1, run.SchemaVersion);
        Assert.Equal(42, run.RowsLoaded);
        Assert.Equal(1.5, run.DurationSeconds); // 1500 ms -> seconds
    }

    [Fact]
    public void RunFromJson_File_MapsDataSetConvention()
    {
        var run = CatalogProjection.RunFromJson(Json("""
            {
              "schemaVersion": 1,
              "flowKind": "file",
              "flowName": "orders",
              "runId": "33333333-3333-3333-3333-333333333333",
              "success": true,
              "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "rowsLoaded": 5, "dataSetConvention": "filename dates; month-first (inferred from file set)" }
            }
            """), Repo);

        Assert.NotNull(run);
        Assert.Equal("filename dates; month-first (inferred from file set)", run!.DataSetConvention);
    }

    [Fact]
    public void RunFromJson_Ingestion_MapsGranularCountsAndSeconds()
    {
        var run = CatalogProjection.RunFromJson(Json("""
            {
              "flowKind": "ing",
              "flowName": "dbo.Customer",
              "runId": "22222222-2222-2222-2222-222222222222",
              "success": false,
              "writtenUtc": "2026-06-17T11:00:00Z",
              "error": "boom",
              "result": { "rowsInserted": 10, "rowsUpdated": 2, "rowsDeleted": 1, "durationSeconds": 5,
                          "startTimeUtc": "2026-06-17T10:59:55Z", "endTimeUtc": "2026-06-17T11:00:00Z" }
            }
            """), Repo);

        Assert.NotNull(run);
        Assert.False(run!.Success);
        Assert.Equal("boom", run.Error);
        Assert.Equal(10, run.RowsInserted);
        Assert.Equal(2, run.RowsUpdated);
        Assert.Equal(1, run.RowsDeleted);
        Assert.Equal(5, run.DurationSeconds);
        Assert.NotNull(run.StartUtc);
        Assert.NotNull(run.EndUtc);
    }

    [Theory]
    [InlineData("""{ "flowKind": "file", "success": true, "writtenUtc": "2026-06-17T10:00:00Z" }""")] // no runId
    [InlineData("""{ "runId": "33333333-3333-3333-3333-333333333333", "flowKind": "file" }""")]        // no flowName
    [InlineData("""[]""")]                                                                               // not an object
    public void RunFromJson_MissingRequiredHeader_ReturnsNull(string json)
        => Assert.Null(CatalogProjection.RunFromJson(Json(json), Repo));

    [Fact]
    public void Pipeline_UsesStableIdentityAndCarriesFields()
    {
        var repoId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);
        var pipeline = CatalogProjection.Pipeline(
            repoId, "orders", "file", batch: null, "flows/orders.flow.yaml",
            "src-server", "trg-server", "deadbeef", "name: orders", "{\"flow\":{}}", now);

        Assert.Equal(CatalogIdentity.Pipeline(repoId, "orders"), pipeline.Id);
        Assert.Equal(repoId, pipeline.RepoId);
        Assert.Equal("file", pipeline.Kind);
        Assert.Equal("flows/orders.flow.yaml", pipeline.RelativePath);
        Assert.Equal("src-server", pipeline.SourceServer);
        Assert.Equal("trg-server", pipeline.TargetServer);
        Assert.True(pipeline.Active);
        Assert.Equal(now, pipeline.FirstSeenUtc);
    }

    [Fact]
    public void Hash_IsDeterministicLowercaseHex()
    {
        var a = CatalogProjection.Hash("name: orders\n");
        var b = CatalogProjection.Hash("name: orders\n");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Equal(a, a.ToLowerInvariant());
        Assert.NotEqual(a, CatalogProjection.Hash("name: customers\n"));
    }

    [Fact]
    public void MapObject_CarriesIdentityAndKind()
    {
        var node = new LineageObjectNode { Key = "srv|db|dbo|customer", ServerRef = "srv", Database = "db", Schema = "dbo", Name = "Customer", Kind = LineageNodeKind.Table };
        var o = CatalogProjection.MapObject(node, new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("srv|db|dbo|customer", o.Key);
        Assert.Equal("Customer", o.Name);
        Assert.Equal("Table", o.Kind);
        Assert.Equal("db", o.Database);
    }

    [Fact]
    public void Edge_Flow_LinksToRepoScopedPipeline()
    {
        var edge = new LineageEdge { Flow = "orders", Relation = LineageRelation.Writes, ObjectKey = "k", Tier = LineageTier.Declared };
        var e = CatalogProjection.Edge(edge, Repo, "Customer");
        Assert.Equal("Writes", e.Relation);
        Assert.Equal("orders", e.Flow);
        Assert.Equal(CatalogIdentity.Pipeline(Repo, "orders"), e.PipelineId);
        Assert.Equal("Customer", e.ObjectName);
        Assert.Equal("Declared", e.Tier);
    }

    [Fact]
    public void Edge_Module_HasNoFlowOrPipeline()
    {
        var edge = new LineageEdge { ViaModule = "srv|db|dbo|vw", Relation = LineageRelation.Reads, ObjectKey = "k", Tier = LineageTier.Derived };
        var e = CatalogProjection.Edge(edge, Repo, "k");
        Assert.Null(e.Flow);
        Assert.Null(e.PipelineId);
        Assert.Equal("Derived", e.Tier);
    }

    [Fact]
    public void RunFiles_And_RunAssertions_FromResult()
    {
        var root = Json("""
            {
              "flowKind": "file", "flowName": "orders",
              "runId": "44444444-4444-4444-4444-444444444444", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": {
                "processedFiles": [{ "name": "a.csv", "path": "/d/a.csv", "rows": 3, "columns": 2, "sizeBytes": 100 }],
                "assertions": [{ "name": "rowcount", "result": "3", "assertedValue": "3", "evaluated": true }]
              }
            }
            """);
        var runId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var file = Assert.Single(CatalogProjection.RunFiles(root, runId, Repo));
        Assert.Equal("a.csv", file.Name);
        Assert.Equal(3, file.Rows);
        Assert.Equal(2, file.Columns);
        Assert.Equal(runId, file.RunId);
        Assert.Equal(Repo, file.RepoId);

        var assertion = Assert.Single(CatalogProjection.RunAssertions(root, runId, Repo));
        Assert.Equal("rowcount", assertion.Name);
        Assert.Equal("3", assertion.Result);
        Assert.True(assertion.Evaluated);
    }

    [Fact]
    public void RunFiles_EmptyWhenResultHasNone()
        => Assert.Empty(CatalogProjection.RunFiles(
            Json("""{ "flowKind": "ing", "flowName": "x", "runId": "55555555-5555-5555-5555-555555555555", "result": {} }"""),
            Guid.NewGuid(), Repo));

    [Fact]
    public void RunFiles_ColumnCountAboveIntMax_SaturatesInsteadOfOverflowing()
    {
        // A column count beyond int range must not wrap to a negative value; it saturates at int.MaxValue.
        var root = Json("""
            {
              "flowKind": "file", "flowName": "wide",
              "runId": "66666666-6666-6666-6666-666666666666", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "processedFiles": [{ "name": "wide.csv", "rows": 1, "columns": 3000000000 }] }
            }
            """);

        var file = Assert.Single(CatalogProjection.RunFiles(root, Guid.Parse("66666666-6666-6666-6666-666666666666"), Repo));
        Assert.Equal(int.MaxValue, file.Columns);
    }

    [Fact]
    public void RunFromJson_SchemaVersionAboveIntMax_SaturatesInsteadOfOverflowing()
    {
        var run = CatalogProjection.RunFromJson(Json("""
            {
              "flowKind": "file", "flowName": "orders",
              "runId": "77777777-7777-7777-7777-777777777777", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "schemaVersion": 5000000000
            }
            """), Repo);

        Assert.NotNull(run);
        Assert.Equal(int.MaxValue, run!.SchemaVersion);
    }

    [Fact]
    public void FlowDependency_LinksBothEndpointsToRepoScopedPipelines()
    {
        var dependency = new LineageFlowDependency { FromFlow = "a", ToFlow = "b", ViaObjects = ["srv|db|dbo|staging"] };
        var d = CatalogProjection.FlowDependency(dependency, Repo, "Staging");
        Assert.Equal("a", d.FromFlow);
        Assert.Equal("b", d.ToFlow);
        Assert.Equal(CatalogIdentity.Pipeline(Repo, "a"), d.FromPipelineId);
        Assert.Equal(CatalogIdentity.Pipeline(Repo, "b"), d.ToPipelineId);
        Assert.Equal("Staging", d.ViaObjects);
    }

    [Fact]
    public void RunFiles_AlsoMapsExportFiles_DerivingNameFromPath()
    {
        var runId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var file = Assert.Single(CatalogProjection.RunFiles(Json($$"""
            {
              "flowKind": "exp", "flowName": "orders-export", "runId": "{{runId}}",
              "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "files": [{ "path": "C:\\out\\orders_2024-01.csv", "rows": 100, "bytes": 2048 }] }
            }
            """), runId, Repo));

        Assert.Equal("orders_2024-01.csv", file.Name); // last path segment
        Assert.Equal("C:\\out\\orders_2024-01.csv", file.Path);
        Assert.Equal(100, file.Rows);
        Assert.Equal(0, file.Columns); // exports report no column count
        Assert.Equal(2048, file.SizeBytes);
    }

    [Fact]
    public void RunFiles_MapsCopyFiles_WithLocationSizeAndHash()
    {
        // A copy run reports files[{ location, sizeBytes, hash }] (distinct from an export's path/rows/bytes shape).
        var runId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        var file = Assert.Single(CatalogProjection.RunFiles(Json($$"""
            {
              "flowKind": "cpy", "flowName": "BB_copy", "runId": "{{runId}}",
              "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "files": [{ "location": "abfss://fs@acct.dfs.core.windows.net/raw/bb/detail.json", "sizeBytes": 512, "hash": "9e107d9d372bb6826bd81d3542a419d6" }] }
            }
            """), runId, Repo));

        Assert.Equal("detail.json", file.Name); // last segment of the location
        Assert.Equal("abfss://fs@acct.dfs.core.windows.net/raw/bb/detail.json", file.Path);
        Assert.Equal(0, file.Columns);
        Assert.Equal(512, file.SizeBytes);
        Assert.Equal("9e107d9d372bb6826bd81d3542a419d6", file.Hash);
    }

    [Fact]
    public void RunStatements_Orders_SqlTrace_ThenDdl_WithoutDoubleCountingSurrogates()
    {
        var runId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var statements = CatalogProjection.RunStatements(Json($$"""
            {
              "flowKind": "ing", "flowName": "x", "runId": "{{runId}}", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": {
                "sqlTrace": [ { "sequence": 1, "step": "staging.create", "sql": "CREATE TABLE #s" },
                              { "sequence": 2, "step": "upsert.update", "sql": "UPDATE t" },
                              { "sequence": 3, "step": "surrogate-key.DW.dim.Customer", "sql": "INSERT sk" } ],
                "ddlExecuted": [ "ALTER TABLE t ADD c int" ],
                "surrogateKeys": [ { "surrogateTable": "DW.dim.Customer", "statements": [ "INSERT sk" ] } ]
              }
            }
            """), runId, Repo);

        // The surrogate statement is in sqlTrace already; the surrogateKeys[].statements must NOT be re-added.
        Assert.Equal(4, statements.Count);
        Assert.Equal([1, 2, 3, 4], statements.Select(s => s.Ordinal)); // global, projection order
        Assert.Equal("staging.create", statements[0].Step);
        Assert.Equal("surrogate-key.DW.dim.Customer", statements[2].Step); // from sqlTrace, not a separate branch
        Assert.Equal("schema.ddl", statements[3].Step);                    // ddlExecuted follows the trace
        Assert.Single(statements, s => s.Sql == "INSERT sk");              // exactly one, no duplication
        Assert.All(statements, s => Assert.Equal(runId, s.RunId));
    }

    [Fact]
    public void RunStatements_CarriesTheTraceTimestamp_AndToleratesItsAbsence()
    {
        var runId = Guid.Parse("99999999-9999-9999-9999-999999999998");
        var statements = CatalogProjection.RunStatements(Json($$"""
            {
              "flowKind": "ing", "flowName": "x", "runId": "{{runId}}", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": {
                "sqlTrace": [ { "sequence": 1, "timestampUtc": "2026-07-08T12:00:00.123Z", "step": "staging.create", "sql": "CREATE TABLE #s" },
                              { "sequence": 2, "step": "upsert.update", "sql": "UPDATE t" } ]
              }
            }
            """), runId, Repo);

        Assert.Equal(2, statements.Count);
        // The timestamp is the interleave key that places the statement in the Events timeline.
        Assert.Equal(new DateTime(2026, 7, 8, 12, 0, 0, 123, DateTimeKind.Utc), statements[0].TimestampUtc);
        Assert.Null(statements[1].TimestampUtc); // a legacy artifact predating the timestamped trace
    }

    [Fact]
    public void RunEvents_FromTheArtifactEventsArray_InOrder_SkippingMalformedEntries()
    {
        var runId = Guid.Parse("99999999-9999-9999-9999-999999999997");
        var events = CatalogProjection.RunEvents(Json($$"""
            {
              "flowKind": "file", "flowName": "x", "runId": "{{runId}}", "success": true, "writtenUtc": "2026-07-08T10:00:00Z",
              "events": [
                { "timestampUtc": "2026-07-08T09:59:01Z", "level": "info", "step": "source.open",
                  "message": "read 'a.csv' (31 row(s))", "rows": 31 },
                { "timestampUtc": "2026-07-08T09:59:02.5Z", "level": "warning", "message": "index skipped", "elapsedMs": 12.5 },
                { "level": "info", "message": "no timestamp: skipped" },
                { "timestampUtc": "2026-07-08T09:59:03Z", "level": "info", "message": "" }
              ]
            }
            """), runId, Repo);

        Assert.Equal(2, events.Count);
        Assert.Equal([1, 2], events.Select(e => e.Ordinal));
        Assert.Equal("info", events[0].Level);
        Assert.Equal("source.open", events[0].Step);
        Assert.Equal("read 'a.csv' (31 row(s))", events[0].Message);
        Assert.Equal(31, events[0].Rows);
        Assert.Null(events[0].ElapsedMs);
        Assert.Equal("warning", events[1].Level);
        Assert.Null(events[1].Step); // a flow-level event with no stage
        Assert.Equal(12.5, events[1].ElapsedMs);
        Assert.All(events, e => Assert.Equal(runId, e.RunId));
        Assert.All(events, e => Assert.Equal(Repo, e.RepoId));
    }

    [Fact]
    public void RunEvents_WithoutTheArray_ProjectsNothing()
    {
        var runId = Guid.Parse("99999999-9999-9999-9999-999999999996");
        Assert.Empty(CatalogProjection.RunEvents(Json($$"""
            { "flowKind": "scm", "flowName": "x", "runId": "{{runId}}", "success": true, "writtenUtc": "2026-07-08T10:00:00Z", "result": {} }
            """), runId, Repo));
    }

    [Fact]
    public void RunSurrogateKeys_FromResult()
    {
        var runId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var key = Assert.Single(CatalogProjection.RunSurrogateKeys(Json($$"""
            {
              "flowKind": "ing", "flowName": "x", "runId": "{{runId}}", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "surrogateKeys": [ { "surrogateKeyId": 7, "surrogateTable": "DW.dim.Customer",
                          "surrogateColumn": "CustomerKey", "isRemote": true, "keysGenerated": 5, "rowsStamped": 10, "executed": true } ] }
            }
            """), runId, Repo));

        Assert.Equal(7, key.SurrogateKeyId);
        Assert.Equal("DW.dim.Customer", key.SurrogateTable);
        Assert.Equal("CustomerKey", key.SurrogateColumn);
        Assert.True(key.IsRemote);
        Assert.Equal(5, key.KeysGenerated);
        Assert.Equal(10, key.RowsStamped);
        Assert.True(key.Executed);
    }

    [Fact]
    public void RunHealthCheckMetrics_FromResult()
    {
        var runId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var metric = Assert.Single(CatalogProjection.RunHealthCheckMetrics(Json($$"""
            {
              "flowKind": "hc", "flowName": "x", "runId": "{{runId}}", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "result": { "metricResults": [ { "name": "RowCount", "seriesPoints": 30, "imputedPoints": 2, "immaturePoints": 1,
                          "anomalies": 3, "levelShifts": 1, "modelTrained": true, "modelTrainer": "AutoML" } ] }
            }
            """), runId, Repo));

        Assert.Equal("RowCount", metric.Name);
        Assert.Equal(30, metric.SeriesPoints);
        Assert.Equal(2, metric.ImputedPoints);
        Assert.Equal(1, metric.ImmaturePoints);
        Assert.Equal(3, metric.Anomalies);
        Assert.Equal(1, metric.LevelShifts);
        Assert.True(metric.ModelTrained);
        Assert.Equal("AutoML", metric.ModelTrainer);
    }

    [Fact]
    public void RunFromJson_MapsHost_ForPerNodeAttribution()
    {
        var run = CatalogProjection.RunFromJson(Json("""
            {
              "flowKind": "file", "flowName": "orders",
              "runId": "aaaaaaaa-0000-0000-0000-00000000000a", "success": true, "writtenUtc": "2026-06-17T10:00:00Z",
              "host": "build-node-7"
            }
            """), Repo);

        Assert.NotNull(run);
        Assert.Equal("build-node-7", run!.Host);
    }

    [Fact]
    public void MapObject_CarriesModuleDefinition()
    {
        var node = new LineageObjectNode
        {
            Key = "srv|db|dbo|vw", ServerRef = "srv", Database = "db", Schema = "dbo", Name = "vw",
            Kind = LineageNodeKind.View, Definition = "CREATE VIEW dbo.vw AS SELECT 1 AS x",
        };
        var o = CatalogProjection.MapObject(node, new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("CREATE VIEW dbo.vw AS SELECT 1 AS x", o.Definition);
    }
}
