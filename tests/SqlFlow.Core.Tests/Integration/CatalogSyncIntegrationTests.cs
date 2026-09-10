using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end shadow-catalog sync against the real sink: <see cref="CatalogDatabase.MigrateAsync"/> provisions the
/// EF-managed <c>catalog</c> schema in the database, then a sync projects a temp YAML estate and its on-disk
/// run.json into it. Asserts the pipeline (with its queryable definition JSON) and the run land, that re-syncing
/// is idempotent (no duplicate runs), and that removing a flow from the estate deactivates its pipeline while its
/// run history survives. Cleans up its own repo's rows so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catsync_" + Guid.NewGuid().ToString("N"));

    public CatalogSyncIntegrationTests() => Directory.CreateDirectory(_dir);

    private void WriteFlow(string flowName, string relativePath)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: __NAME__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: CatOrders
            """.Replace("__NAME__", flowName, StringComparison.Ordinal));
    }

    private void WriteRun(string flowName, Guid runId, bool success)
    {
        var dir = Path.Combine(_dir, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName), $"20260617-100000_{runId.ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.json"), $$"""
            {
              "schemaVersion": 1,
              "flowKind": "file",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": {{(success ? "true" : "false")}},
              "writtenUtc": "2026-06-17T10:00:00Z",
              "result": {
                "rowsLoaded": 5, "totalMs": 100.0,
                "processedFiles": [{ "name": "data.csv", "path": "./data.csv", "rows": 5, "columns": 2, "sizeBytes": 50 }],
                "assertions": [{ "name": "nonEmpty", "result": "5", "assertedValue": "5", "evaluated": true }]
              }
            }
            """);
    }

    [SkippableFact]
    public async Task Migrate_IsIdempotent_AndLeavesNoPending()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await CatalogDatabase.MigrateAsync(cs); // again: a no-op, never an error

        var (applied, pending) = await CatalogDatabase.StatusAsync(cs);
        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    [SkippableFact]
    public async Task Sync_ProjectsPipelineAndRun_IsIdempotent_AndDeletesRemoved()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_test_" + suffix;
        var flowName = "cat_orders_" + suffix;
        var runId = Guid.NewGuid();
        var repoId = FlowIdentity.FromName(repo);

        WriteFlow(flowName, "flows/orders.flow.yaml");
        WriteRun(flowName, runId, success: true);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var first = await new CatalogSync().SyncAsync(db, _dir, repo, "https://example/repo.git", DateTime.UtcNow);
                Assert.Equal(1, first.PipelinesAdded);
                Assert.Equal(1, first.RunsAdded);
                Assert.Equal(1, first.RunFilesAdded);       // the run.json processedFiles
                Assert.Equal(1, first.RunAssertionsAdded);  // the run.json assertions
                Assert.True(first.LineageEdges >= 1);       // the flow writes its target table -> at least one edge
                Assert.True(first.ObjectsUpserted >= 1);
                Assert.False(first.LineageConnected);       // offline (no --connect)
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var pipeline = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), pipeline.Id);
                Assert.Equal(repoId, pipeline.RepoId);
                Assert.Equal("file", pipeline.Kind);
                Assert.True(pipeline.Active);
                Assert.Contains(flowName, pipeline.DefinitionJson, StringComparison.Ordinal); // the YAML mapped into queryable JSON
                Assert.False(string.IsNullOrEmpty(pipeline.Yaml));

                var run = await db.Runs.SingleAsync(r => r.RunId == runId);
                Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), run.PipelineId); // joins to the pipeline
                Assert.Equal(repoId, run.RepoId);
                Assert.Equal(5, run.RowsLoaded);

                // Run drill-down detail landed and joins back to the run.
                Assert.True(await db.RunFiles.AnyAsync(f => f.RunId == runId && f.Name == "data.csv"));
                Assert.True(await db.RunAssertions.AnyAsync(a => a.RunId == runId && a.Name == "nonEmpty"));

                // Lineage edges are repo-scoped and join to the pipeline; objects are queryable by name across repos.
                Assert.True(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId));
                Assert.True(await db.Objects.AnyAsync());

                Assert.True(await db.Repos.AnyAsync(r => r.Id == repoId && r.Name == repo));
            }

            // Re-sync: unchanged flow + already-known run -> nothing added.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(0, again.PipelinesAdded);
                Assert.Equal(0, again.RunsAdded);
                Assert.Equal(1, again.RunsSkipped);
            }

            // The flow leaves the estate -> its pipeline is deleted (no tombstone), its run history is kept.
            File.Delete(Path.Combine(_dir, "flows", "orders.flow.yaml"));
            await using (var db = CatalogDatabase.Create(cs))
            {
                var removed = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(1, removed.PipelinesDeleted);
                Assert.False(await db.Pipelines.AnyAsync(p => p.Name == flowName));  // deleted, not tombstoned
                Assert.True(await db.Runs.AnyAsync(r => r.RunId == runId));          // run traces kept (they carry the flow name)
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_EnrichesObjectScriptAndColumns_FromPersistedRunStatements()
    {
        // The control-plane posture: a run recorded by an earlier write-back leaves its generated CREATE
        // statement in the catalog (catalog.RunStatement), but its run.json is git-ignored and so absent from
        // the estate the full sync scans. The declared tier still surfaces the target object; the offline
        // enrichment fills that object's generating script and column dictionary from the persisted trace, so an
        // object is not a code-less, column-less skeleton offline. The table name is unique per run to keep the
        // global object registry isolated across test runs.
        //
        // Crucially, the CREATE TABLE lives in an OLDER run while a NEWER run only re-emits a view (the engine's
        // real pattern: a table's DDL runs once, a `CREATE OR ALTER VIEW` runs every load). The enrichment must
        // walk run history newest-first until it finds each object's creating statement, not read the latest run
        // alone, so this seeds exactly that shape.
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_enrich_" + suffix;
        var flowName = "cat_enrich_orders_" + suffix;
        var targetTable = "EnrichTarget_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var olderRunId = Guid.NewGuid();
        var newerRunId = Guid.NewGuid();
        const string serverRef = "${env:SQLFlowSinkConStr}";

        var path = Path.Combine(_dir, "flows", "enrich.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: {{serverRef}}
              schema: dbo
              table: {{targetTable}}
            """);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // Seed the persisted run trace WITHOUT any run.json on disk. The OLDER run created the table; the
            // NEWER run only regenerated a view and never re-emitted the table's DDL.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Runs.Add(new CatalogRun
                {
                    RunId = olderRunId,
                    PipelineId = pipelineId,
                    RepoId = repoId,
                    FlowName = flowName,
                    FlowKind = "file",
                    Success = true,
                    Status = RunStatuses.Succeeded,
                    WrittenUtc = DateTime.UtcNow.AddHours(-2),
                });
                db.RunStatements.Add(new CatalogRunStatement
                {
                    RunId = olderRunId,
                    RepoId = repoId,
                    Ordinal = 1,
                    Step = "schema.apply-ddl",
                    Sql = $"CREATE TABLE [dbo].[{targetTable}] ([OrderID] int NOT NULL, [Customer] nvarchar(100) NULL);",
                });
                db.Runs.Add(new CatalogRun
                {
                    RunId = newerRunId,
                    PipelineId = pipelineId,
                    RepoId = repoId,
                    FlowName = flowName,
                    FlowKind = "file",
                    Success = true,
                    Status = RunStatuses.Succeeded,
                    WrittenUtc = DateTime.UtcNow,
                });
                db.RunStatements.Add(new CatalogRunStatement
                {
                    RunId = newerRunId,
                    RepoId = repoId,
                    Ordinal = 1,
                    Step = "transform.view",
                    Sql = $"CREATE OR ALTER VIEW [dbo].[v_{targetTable}] AS SELECT CAST([OrderID] AS int) AS [OrderID] FROM [dbo].[{targetTable}];",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var target = await db.Objects.SingleAsync(o => o.ServerRef == serverRef && o.Schema == "dbo" && o.Name == targetTable);
                Assert.NotNull(target.Script);
                Assert.Contains("CREATE TABLE", target.Script!, StringComparison.Ordinal);
                Assert.Equal(nameof(SqlFlow.Core.Lineage.LineageTier.Observed), target.ScriptTier);

                var columns = await db.ObjectColumns.Where(c => c.ObjectKey == target.Key).OrderBy(c => c.Ordinal).ToListAsync();
                Assert.Collection(columns,
                    c => { Assert.Equal("OrderID", c.Name); Assert.Equal("int", c.DataType); Assert.False(c.Nullable); },
                    c => { Assert.Equal("Customer", c.Name); Assert.Equal("nvarchar(100)", c.DataType); Assert.True(c.Nullable); });
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var key = await db.Objects.Where(o => o.ServerRef == serverRef && o.Name == targetTable).Select(o => o.Key).ToListAsync();
            await db.ObjectColumns.Where(c => key.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef && o.Name == targetTable).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_ExpandsEmbeddedHealthCheck_IntoASiblingPipeline()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_hc_" + suffix;
        var flowName = "cat_hcload_" + suffix;
        var checkName = flowName + "_hc";
        var repoId = FlowIdentity.FromName(repo);

        var path = Path.Combine(_dir, "flows", "orders_hc.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            flowType: ing
            name: {{flowName}}
            batch: HC
            connections:
              src: ${env:SQLFlowSinkConStr}
              dwh: ${env:SQLFlowSinkConStr}
            source: { server: src, object: db.dbo.Orders }
            target: { server: dwh, object: db.dbo.Orders_DW }
            load: { keyColumns: [OrderID] }
            healthCheck:
              dateColumn: OrderDate
              baseValue: COUNT(*)
            """);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // One file, two pipelines: the load and its derived (mode: manual by default) health check.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var first = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(2, first.PipelinesAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var load = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                var check = await db.Pipelines.SingleAsync(p => p.Name == checkName);
                Assert.Equal("ing", load.Kind);
                Assert.Equal(PipelineExecutionModes.Auto, load.ExecutionMode);
                Assert.Equal("hc", check.Kind);
                Assert.Equal(PipelineExecutionModes.Manual, check.ExecutionMode);
                Assert.Equal(load.RelativePath, check.RelativePath); // one file, shared by both pipelines
                Assert.Equal("HC", check.Batch);                     // inherited from the flow
                Assert.True(check.Active);

                // The check reads what the load writes, so lineage orders it after the load.
                Assert.True(await db.FlowDependencies.AnyAsync(
                    d => d.RepoId == repoId && d.FromFlow == flowName && d.ToFlow == checkName));
            }

            // Idempotent: nothing re-added on an unchanged estate.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(0, again.PipelinesAdded);
            }
        }
        finally
        {
            File.Delete(path);
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_HonorsExcludedFlowSelection_UnderNoTracking_DeactivatesAndReactivates()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sel_" + suffix;
        var flowA = "cat_sel_a_" + suffix;
        var flowB = "cat_sel_b_" + suffix;
        var repoId = FlowIdentity.FromName(repo);

        WriteFlow(flowA, "flows/a.flow.yaml");
        WriteFlow(flowB, "flows/b.flow.yaml");
        await CatalogDatabase.MigrateAsync(cs);

        // The control plane pools its catalog context with QueryTrackingBehavior.NoTracking; the sync's
        // update/deactivate mutations MUST still persist. Running the whole lifecycle through a NoTracking context
        // is the regression guard for that fix (without it, the deactivation below silently no-ops).
        static CatalogDbContext NoTracking(string connectionString)
        {
            var db = CatalogDatabase.Create(connectionString);
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return db;
        }

        var excludeB = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "flows/b.flow.yaml" };

        try
        {
            // 1. Import with flow B excluded: only A becomes a pipeline; B is never projected.
            await using (var db = NoTracking(cs))
            {
                var r = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, excludedFlowPaths: excludeB);
                Assert.Equal(1, r.PipelinesAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await db.Pipelines.AnyAsync(p => p.Name == flowA && p.Active));
                Assert.False(await db.Pipelines.AnyAsync(p => p.Name == flowB));
            }

            // 2. Re-include B (no exclusion): it appears and is active.
            await using (var db = NoTracking(cs))
            {
                var r = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, excludedFlowPaths: null);
                Assert.Equal(1, r.PipelinesAdded); // B added; A unchanged
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await db.Pipelines.AnyAsync(p => p.Name == flowB && p.Active));
            }

            // 3. Exclude B again: the previously-imported pipeline is DEACTIVATED (the mutation persists under
            // NoTracking), and its history would survive. A stays active.
            await using (var db = NoTracking(cs))
            {
                var r = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, excludedFlowPaths: excludeB);
                Assert.Equal(1, r.PipelinesDeactivated);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await db.Pipelines.AnyAsync(p => p.Name == flowA && p.Active));
                Assert.False((await db.Pipelines.SingleAsync(p => p.Name == flowB)).Active);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_HealsTheDatabaselessTwin_WhenTheIdentityGainsItsDatabase()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_heal_" + suffix;
        var flowName = "cat_heal_orders_" + suffix;
        var table = "HealOrders_" + suffix;
        var variable = "SQLFLOW_TEST_SINK_" + suffix.ToUpperInvariant();
        var repoId = FlowIdentity.FromName(repo);
        var serverRef = "${env:" + variable + "}";
        var weakKey = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, null, "dbo", table);
        var strongKey = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, "SinkDb", "dbo", table);

        var flowPath = Path.Combine(_dir, "flows", "heal.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(flowPath)!);
        File.WriteAllText(flowPath, $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:{{variable}}}
              schema: dbo
              table: {{table}}
            """);

        await CatalogDatabase.MigrateAsync(cs);
        try
        {
            // First sync with the reference unresolvable: the object lands under the database-less key,
            // exactly the pre-resolution catalog state this healing exists for.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.True(await db.Objects.AnyAsync(o => o.Key == weakKey));
            }

            // The reference becomes resolvable: the identity gains its database, and the weak twin
            // (no longer referenced by any repo's edges) is superseded and removed. The flow document is also
            // touched (a comment changes its content hash), because the sync recomputes lineage only when a
            // lineage input it can observe changed: pipeline content, the flow set, or new run artifacts. An
            // environment-only change rides along with the next content change or connected sync.
            Environment.SetEnvironmentVariable(variable, "Server=localhost;Initial Catalog=SinkDb;Integrated Security=true;");
            File.AppendAllText(flowPath, $"{Environment.NewLine}# sink reference resolvable since this revision{Environment.NewLine}");
            await using (var db = CatalogDatabase.Create(cs))
            {
                var healed = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(1, healed.ObjectsSuperseded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.Objects.AnyAsync(o => o.Key == weakKey));
                var strong = await db.Objects.SingleAsync(o => o.Key == strongKey);
                Assert.Equal("sinkdb", strong.Database);
                Assert.True(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId && e.ObjectKey == strongKey));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == weakKey || o.Key == strongKey).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_OfflineResync_AdoptsTheResolvedIdentity_InsteadOfRecreatingTheTwin()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_adopt_" + suffix;
        var flowName = "cat_adopt_orders_" + suffix;
        var table = "AdoptOrders_" + suffix;
        var variable = "SQLFLOW_TEST_SINK_" + suffix.ToUpperInvariant();
        var repoId = FlowIdentity.FromName(repo);
        var serverRef = "${env:" + variable + "}";
        var weakKey = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, null, "dbo", table);
        var strongKey = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, "SinkDb", "dbo", table);

        var flowPath = Path.Combine(_dir, "flows", "adopt.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(flowPath)!);
        File.WriteAllText(flowPath, $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:{{variable}}}
              schema: dbo
              table: {{table}}
            """);

        await CatalogDatabase.MigrateAsync(cs);
        try
        {
            // First sync with the reference resolvable: the connected knowledge lands the object under its
            // database-qualified identity.
            Environment.SetEnvironmentVariable(variable, "Server=localhost;Initial Catalog=SinkDb;Integrated Security=true;");
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.True(await db.Objects.AnyAsync(o => o.Key == strongKey));
            }

            // The reference becomes unresolvable again (an offline sync), and the flow content changes so the
            // lineage recomputes: the report only knows the database-less identity, and the adoption pass must
            // land it on the registry's resolved row instead of splitting a weak twin back out.
            Environment.SetEnvironmentVariable(variable, null);
            File.AppendAllText(flowPath, $"{Environment.NewLine}# resynced without the sink reference resolvable{Environment.NewLine}");
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.Objects.AnyAsync(o => o.Key == weakKey));
                Assert.True(await db.Objects.AnyAsync(o => o.Key == strongKey));
                Assert.True(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId && e.ObjectKey == strongKey));
                Assert.False(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId && e.ObjectKey == weakKey));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == weakKey || o.Key == strongKey).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_MirrorsYamlSchedule_PreservesApiPause_AndRemovesItWhenItLeavesGit()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sch_" + suffix;
        var flowName = "cat_sch_orders_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var scheduleId = CatalogIdentity.YamlSchedule(repoId, flowName);
        var flowPath = Path.Combine(_dir, "flows", "orders.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(flowPath)!);

        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // A flow declaring a cron schedule in git.
            File.WriteAllText(flowPath, $$"""
                name: {{flowName}}
                schedule:
                  cron: "0 6 * * *"
                  timezone: "UTC"
                source:
                  type: csv
                  location: ./data.csv
                target:
                  connection: ${env:SQLFlowSinkConStr}
                  schema: dbo
                  table: CatOrders
                """);

            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var schedule = await db.Schedules.SingleAsync(s => s.Id == scheduleId);
                Assert.Equal("yaml", schedule.Source);
                Assert.Equal("0 6 * * *", schedule.Cron);
                Assert.Equal("UTC", schedule.Timezone);
                Assert.True(await db.ScheduleMembers.AnyAsync(m => m.ScheduleId == scheduleId && m.PipelineId == pipelineId));
                Assert.True(schedule.Enabled);
                Assert.NotNull(schedule.NextFireUtc);

                // An operator pauses the git schedule through the API.
                await ScheduleStore.SetPausedAsync(db, scheduleId, paused: true, nextFireUtcOnResume: null, DateTime.UtcNow);
            }

            // Re-sync the unchanged estate: the pause set through the API must survive.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.True((await db.Schedules.SingleAsync(s => s.Id == scheduleId)).Paused);
            }

            // The schedule leaves git (the flow stays, the schedule block is removed): the yaml schedule is removed.
            File.WriteAllText(flowPath, $$"""
                name: {{flowName}}
                source:
                  type: csv
                  location: ./data.csv
                target:
                  connection: ${env:SQLFlowSinkConStr}
                  schema: dbo
                  table: CatOrders
                """);
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.False(await db.Schedules.AnyAsync(s => s.Id == scheduleId));
                // The pipeline itself stays (only its schedule left git).
                Assert.True(await db.Pipelines.AnyAsync(p => p.Id == pipelineId && p.Active));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private void WriteIngFlow(string flowName, string relativePath, string sourceObject, string targetObject)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            flowType: ing
            name: __NAME__
            connections:
              dwh: ${env:SqlFlowSinkConStr}
            source:
              server: dwh
              object: __SRC__
            target:
              server: dwh
              object: __TRG__
            load:
              keyColumns: [Id]
            """
            .Replace("__NAME__", flowName, StringComparison.Ordinal)
            .Replace("__SRC__", sourceObject, StringComparison.Ordinal)
            .Replace("__TRG__", targetObject, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Sync_Lineage_OrdersFlowsIntoWaves_AndRecordsDependencies()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_wave_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var flowA = "wave_a_" + suffix;
        var flowB = "wave_b_" + suffix;

        // B reads the table A writes, so B must run in a later wave than A (declared lineage, offline).
        WriteIngFlow(flowA, "flows/a.flow.yaml", $"TestDB.dbo.Raw_{suffix}", $"TestDB.dbo.Staging_{suffix}");
        WriteIngFlow(flowB, "flows/b.flow.yaml", $"TestDB.dbo.Staging_{suffix}", $"TestDB.dbo.Final_{suffix}");
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(2, result.PipelinesAdded);
                Assert.True(result.Waves >= 2, $"expected >=2 waves, got {result.Waves}");
                Assert.True(result.FlowDependencies >= 1, $"expected >=1 dependency, got {result.FlowDependencies}");
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var a = await db.Pipelines.SingleAsync(p => p.Name == flowA);
                var b = await db.Pipelines.SingleAsync(p => p.Name == flowB);
                Assert.True(a.Wave >= 0);
                Assert.True(b.Wave > a.Wave, $"expected B (wave {b.Wave}) to run after A (wave {a.Wave})");
                Assert.True(await db.FlowDependencies.AnyAsync(d => d.RepoId == repoId && d.FromPipelineId == a.Id && d.ToPipelineId == b.Id));

                // The stamped object levels mirror the movement chain: the raw source (nothing produces it) is
                // level 0, the staging table A writes is level 1, and the final table B writes is level 2.
                var levelByName = await db.Objects
                    .Where(o => o.Name == $"Raw_{suffix}" || o.Name == $"Staging_{suffix}" || o.Name == $"Final_{suffix}")
                    .ToDictionaryAsync(o => o.Name, o => o.Level);
                Assert.Equal((int?)0, levelByName[$"Raw_{suffix}"]);
                Assert.Equal((int?)1, levelByName[$"Staging_{suffix}"]);
                Assert.Equal((int?)2, levelByName[$"Final_{suffix}"]);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task RecordRun_RecordsRunAndPipeline_WithoutFullSync_AndIsIdempotent()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_rec_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var flowName = "rec_orders_" + suffix;
        var runId = Guid.NewGuid();
        var flowFile = Path.Combine(_dir, "flows", "orders.flow.yaml");

        WriteFlow(flowName, "flows/orders.flow.yaml");
        // A run.json carrying a host, written exactly where the engine would put it.
        var runDir = Path.Combine(_dir, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName), $"20260617-100000_{runId.ToString("N")[..8]}");
        Directory.CreateDirectory(runDir);
        var runJson = Path.Combine(runDir, "run.json");
        File.WriteAllText(runJson, $$"""
            {
              "schemaVersion": 1, "flowKind": "file", "flowName": "{{flowName}}", "runId": "{{runId}}",
              "success": true, "writtenUtc": "2026-06-17T10:00:00Z", "host": "node-test",
              "result": { "rowsLoaded": 5, "totalMs": 100.0,
                "processedFiles": [{ "name": "data.csv", "path": "./data.csv", "rows": 5, "columns": 2, "sizeBytes": 50 }],
                "assertions": [{ "name": "nonEmpty", "result": "5", "assertedValue": "5", "evaluated": true }] }
            }
            """);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // No full SyncAsync: the per-run write-back alone records the run AND ensures the pipeline row.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().RecordRunAsync(db, flowFile, runJson, repo, null, DateTime.UtcNow);
                Assert.Equal(PipelineChange.Added, result.PipelineChange);
                Assert.True(result.RunRecorded);
                Assert.Equal(1, result.RunFilesAdded);
                Assert.Equal(1, result.RunAssertionsAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var pipeline = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                Assert.True(pipeline.Active);
                Assert.Equal(-1, pipeline.Wave); // the write-back does NOT compute lineage; wave stays "not computed"

                var run = await db.Runs.SingleAsync(r => r.RunId == runId);
                Assert.Equal("node-test", run.Host); // per-node attribution from the run.json header
                Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), run.PipelineId);
                Assert.True(await db.RunFiles.AnyAsync(f => f.RunId == runId));
                Assert.True(await db.RunAssertions.AnyAsync(a => a.RunId == runId));
            }

            // Recording the same run again is a no-op (a run is immutable; the unchanged flow only re-affirms).
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().RecordRunAsync(db, flowFile, runJson, repo, null, DateTime.UtcNow);
                Assert.False(again.RunRecorded);
                Assert.Equal(PipelineChange.Unchanged, again.PipelineChange);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task RecordRun_LeavesSchedulesToTheFullSync()
    {
        // A schedule owns a MEMBER SET, and membership is a repo-wide fact: one flow's document cannot say who else
        // joined the name. So the per-run write-back deliberately does not touch schedules at all; writing one from a
        // single file would either invent an empty member set or clobber the one the estate scan established. This
        // pins that contract: a write-back neither removes an established schedule nor rewrites its cadence, and the
        // full sync remains the only thing that reconciles them.
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sched_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var flowName = "sched_orders_" + suffix;
        var flowFile = Path.Combine(_dir, "flows", "orders.flow.yaml");
        // An unnamed inline block is named after its declaring flow, so that is this schedule's identity.
        var scheduleId = CatalogIdentity.YamlSchedule(repoId, flowName);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);

        WriteScheduledFlow(flowName, "flows/orders.flow.yaml", "0 6 * * *");
        var runId = Guid.NewGuid();
        var runDir = Path.Combine(_dir, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName), $"20260617-100000_{runId.ToString("N")[..8]}");
        Directory.CreateDirectory(runDir);
        var runJson = Path.Combine(runDir, "run.json");
        File.WriteAllText(runJson, $$"""
            {
              "schemaVersion": 1, "flowKind": "file", "flowName": "{{flowName}}", "runId": "{{runId}}",
              "success": true, "writtenUtc": "2026-06-17T10:00:00Z", "host": "node-test",
              "result": { "rowsLoaded": 1, "totalMs": 10.0 }
            }
            """);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // The full sync establishes the schedule, with the declaring flow as its one member.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                var schedule = await db.Schedules.SingleAsync(s => s.Id == scheduleId);
                Assert.Equal("0 6 * * *", schedule.Cron);
                Assert.Equal("yaml", schedule.Source);
                Assert.Equal(flowName, schedule.Name);
                Assert.True(schedule.Enabled);
                Assert.NotNull(schedule.NextFireUtc);
                Assert.True(await db.ScheduleMembers.AnyAsync(m => m.ScheduleId == scheduleId && m.PipelineId == pipelineId));
            }

            // A cron change plus a write-back: the run lands, the schedule is untouched. The write-back is not the
            // authority on schedules, so it must not act on what it can only half-see.
            WriteScheduledFlow(flowName, "flows/orders.flow.yaml", "30 7 * * *");
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().RecordRunAsync(db, flowFile, runJson, repo, null, DateTime.UtcNow);
                var schedule = await db.Schedules.SingleAsync(s => s.Id == scheduleId);
                Assert.Equal("0 6 * * *", schedule.Cron);
                Assert.True(await db.Runs.AnyAsync(r => r.RunId == runId));
            }

            // The full sync is what reconciles it.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                var schedule = await db.Schedules.SingleAsync(s => s.Id == scheduleId);
                Assert.Equal("30 7 * * *", schedule.Cron);
            }

            // Removing the schedule: block from the YAML removes it, with its memberships, on the next full sync.
            WriteFlow(flowName, "flows/orders.flow.yaml");
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.False(await db.Schedules.AnyAsync(s => s.Id == scheduleId));
                Assert.False(await db.ScheduleMembers.AnyAsync(m => m.ScheduleId == scheduleId));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private void WriteScheduledFlow(string flowName, string relativePath, string cron)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: __NAME__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: CatOrders
            schedule:
              cron: "__CRON__"
            """
            .Replace("__NAME__", flowName, StringComparison.Ordinal)
            .Replace("__CRON__", cron, StringComparison.Ordinal));
    }

    private void WriteFlowWithConnection(string flowName, string relativePath, string connection)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: __NAME__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: __CONN__
              schema: dbo
              table: CatOrders
            """.Replace("__NAME__", flowName, StringComparison.Ordinal).Replace("__CONN__", connection, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Sync_RedactsEmbeddedSecret_NeverRestsInCatalog()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sec_" + suffix;
        var flowName = "cat_secret_" + suffix;
        var repoId = FlowIdentity.FromName(repo);

        // A flow that, against policy, inlines a credential. The sync must redact it (and warn), never store it.
        WriteFlowWithConnection(flowName, "flows/secret.flow.yaml", "Server=db;Database=x;User ID=u;Password=topsecret123;TrustServerCertificate=True");
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Contains(result.Warnings, w => w.Contains("credential", StringComparison.OrdinalIgnoreCase));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var pipeline = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                Assert.DoesNotContain("topsecret123", pipeline.Yaml, StringComparison.Ordinal);
                Assert.DoesNotContain("topsecret123", pipeline.DefinitionJson, StringComparison.Ordinal);
                Assert.Contains("[redacted]", pipeline.Yaml, StringComparison.Ordinal);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task OfflineResync_PreservesPreviouslyDerivedEdges()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_keep_" + suffix;
        var flowName = "cat_keep_orders_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        WriteFlow(flowName, "flows/keep.flow.yaml");
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            // Simulate what an earlier CONNECTED pass learned from a module body: a derived edge attributing a
            // procedure's write to the flow. An offline recompute cannot re-derive it (no server to read), so
            // the sync must carry it forward instead of wiping it with the degraded pass.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId,
                    Flow = flowName,
                    PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
                    ViaModule = "@dwh|dw|dbo|usp_build",
                    Relation = "Writes",
                    ObjectKey = "@dwh|dw|dbo|mart",
                    ObjectName = "Mart",
                    Tier = "Derived",
                });
                await db.SaveChangesAsync();
            }

            // Change the flow so the lineage gate recomputes (same content would skip the write entirely).
            WriteFlow(flowName + "_sibling", "flows/keep2.flow.yaml");

            CatalogSyncResult second;
            await using (var db = CatalogDatabase.Create(cs))
            {
                second = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            Assert.Equal(1, second.LineageEdgesPreserved);
            await using (var verify = CatalogDatabase.Create(cs))
            {
                var kept = await verify.LineageEdges.SingleOrDefaultAsync(
                    e => e.RepoId == repoId && e.Tier == "Derived" && e.ObjectKey == "@dwh|dw|dbo|mart");
                Assert.NotNull(kept);
                Assert.Equal(flowName, kept.Flow);
                Assert.Equal("@dwh|dw|dbo|usp_build", kept.ViaModule);
                // The declared tier still refreshed alongside the preserved knowledge.
                Assert.True(await verify.LineageEdges.AnyAsync(e => e.RepoId == repoId && e.Tier == "Declared"));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.PipelineColumns.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
