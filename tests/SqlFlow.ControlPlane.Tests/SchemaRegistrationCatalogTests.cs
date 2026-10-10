using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Core.SchemaRegistration;
using SqlFlow.Dispatch;
using SqlFlow.Lineage.Collection;
using SqlFlow.SqlServer.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A schema registration flow (flowType: sch) against real databases: what it reads from a live database (tables and
/// views in scope, with keys and scripts, and nothing else), what a completed run records in the catalog (objects,
/// columns, and the flow's Registers edges, replaced on the next run), and how a registered object tells a query
/// where to run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaRegistrationCatalogTests
{
    [SkippableFact]
    public async Task Reader_RegistersOnlyTablesAndViewsInScope_WithKeysAndScripts()
    {
        var cs = CatalogTestDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var kept = "schreg_" + suffix;
        var skipped = "schskip_" + suffix;

        await ExecAsync(cs,
            $"CREATE SCHEMA [{kept}];",
            $"CREATE SCHEMA [{skipped}];",
            $"CREATE TABLE [{kept}].[Orders] (OrderId int NOT NULL CONSTRAINT [PK_{kept}_Orders] PRIMARY KEY, Amount decimal(18,2) NULL);",
            $"CREATE VIEW [{kept}].[BigOrders] AS SELECT OrderId, Amount FROM [{kept}].[Orders] WHERE Amount > 100;",
            $"CREATE PROCEDURE [{kept}].[LoadOrders] AS SELECT 1;",
            $"CREATE TABLE [{skipped}].[Ignored] (Id int NOT NULL);");
        try
        {
            var read = await SchemaRegistrationReader.ReadAsync(
                cs, new SchemaRegistrationScope { IncludeSchemas = [kept.ToUpperInvariant(), skipped], ExcludeSchemas = [skipped] });

            var objects = read.Objects.Where(o => o.Schema == kept).OrderBy(o => o.Name).ToList();
            Assert.DoesNotContain(read.Objects, o => o.Schema == skipped);
            Assert.Equal(["BigOrders", "Orders"], objects.Select(o => o.Name));

            var view = objects[0];
            Assert.Equal(Core.Lineage.LineageNodeKind.View, view.Kind);
            Assert.Contains("Amount > 100", view.Script, StringComparison.Ordinal);
            Assert.Empty(view.KeyColumns);
            Assert.Equal(["OrderId", "Amount"], view.Columns.Select(c => c.Name));

            var table = objects[1];
            Assert.Equal(Core.Lineage.LineageNodeKind.Table, table.Kind);
            Assert.Equal(["OrderId"], table.KeyColumns);
            Assert.StartsWith($"CREATE TABLE [{kept}].[Orders]", table.Script, StringComparison.Ordinal);
            Assert.Contains("PRIMARY KEY", table.Script, StringComparison.Ordinal);
            Assert.Equal("decimal(18,2)", table.Columns.Single(c => c.Name == "Amount").DataType);
        }
        finally
        {
            await ExecAsync(cs,
                $"DROP PROCEDURE IF EXISTS [{kept}].[LoadOrders];",
                $"DROP VIEW IF EXISTS [{kept}].[BigOrders];",
                $"DROP TABLE IF EXISTS [{kept}].[Orders];",
                $"DROP TABLE IF EXISTS [{skipped}].[Ignored];",
                $"DROP SCHEMA IF EXISTS [{kept}];",
                $"DROP SCHEMA IF EXISTS [{skipped}];");
        }
    }

    [SkippableFact]
    public async Task CompletedRun_RecordsObjectsAndEdges_ThenReplacesThemOnTheNextRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName, serverRef, pipelineId) = Seed();

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                AddRepoAndPipeline(db, repoId, flowName, serverRef, pipelineId);
                await db.SaveChangesAsync();
            }

            var firstKey = NodeKey.For(serverRef, "Registry", "dbo", "Orders");
            var secondKey = NodeKey.For(serverRef, "Registry", "dbo", "Customers");

            await using (var db = CatalogDatabase.Create(cs))
            {
                // The first run registers a table and a view.
                var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "sch"), DateTime.UtcNow)).RunId;
                Assert.Equal(
                    RunOutcomeStatus.Recorded,
                    (await RunQueueStore.CompleteFromArtifactAsync(db, runId, Artifact(runId, flowName, ("Orders", "Table"), ("OrderLines", "View")), DateTime.UtcNow)).Status);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var order = await db.Objects.AsNoTracking().SingleAsync(o => o.Key == firstKey);
                Assert.Equal("Table", order.Kind);
                Assert.Equal("Id", order.KeyColumns);
                Assert.StartsWith("CREATE TABLE [dbo].[Orders]", order.Script, StringComparison.Ordinal);
                Assert.Equal(2, await db.ObjectColumns.CountAsync(c => c.ObjectKey == firstKey));

                var view = await db.Objects.AsNoTracking()
                    .SingleAsync(o => o.Key == NodeKey.For(serverRef, "Registry", "dbo", "OrderLines"));
                Assert.Equal("View", view.Kind);
                Assert.Equal("SELECT 1 AS Id", view.Definition);

                var edges = await db.LineageEdges.AsNoTracking()
                    .Where(e => e.PipelineId == pipelineId && e.Relation == "Registers")
                    .Select(e => e.ObjectKey)
                    .ToListAsync();
                Assert.Equal(2, edges.Count);
                Assert.Contains(firstKey, edges);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "sch"), DateTime.UtcNow)).RunId;
                Assert.Equal(
                    RunOutcomeStatus.Recorded,
                    (await RunQueueStore.CompleteFromArtifactAsync(db, runId, Artifact(runId, flowName, ("Customers", "Table")), DateTime.UtcNow)).Status);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var edges = await db.LineageEdges.AsNoTracking()
                    .Where(e => e.PipelineId == pipelineId && e.Relation == "Registers")
                    .Select(e => e.ObjectKey)
                    .ToListAsync();
                Assert.Equal([secondKey], edges);

                // The registry is global and additive: the object itself stays, it just no longer resolves.
                Assert.True(await db.Objects.AnyAsync(o => o.Key == firstKey));
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId, serverRef);
        }
    }

    [SkippableFact]
    public async Task RegisteredObjects_DecideWhereAQueryRuns_AndTheReferenceIsDeclared()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName, serverRef, pipelineId) = Seed();
        var key = NodeKey.For(serverRef, "Registry", "dbo", "Orders");

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                AddRepoAndPipeline(db, repoId, flowName, serverRef, pipelineId);
                var now = DateTime.UtcNow;
                db.Objects.Add(new SqlFlow.Catalog.CatalogObject
                {
                    Key = key, ServerRef = serverRef, Database = "Registry", Schema = "dbo", Name = "Orders",
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId, Flow = flowName, PipelineId = pipelineId, Relation = "Registers",
                    ObjectKey = key, ObjectName = "Orders", Tier = "Derived",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var inferred = await DatasourceInference.InferAsync(db, "SELECT 1", [key], CancellationToken.None);
                Assert.Equal(serverRef, inferred.Reference);
                Assert.Equal("Registry", inferred.Database);

                Assert.True(await DatasourceInference.IsDeclaredAsync(db, serverRef, CancellationToken.None));
                Assert.Null(await QuestionExampleEndpoints.SourceRefProblemAsync(db, serverRef, CancellationToken.None));
            }

            // A deactivated registration no longer declares the datasource, nor decides where anything runs.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.Id == pipelineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Active, false));
                Assert.False(await DatasourceInference.IsDeclaredAsync(db, serverRef, CancellationToken.None));
                Assert.Null((await DatasourceInference.InferAsync(db, null, [key], CancellationToken.None)).Reference);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId, serverRef);
        }
    }

    private static (Guid RepoId, string FlowName, string ServerRef, Guid PipelineId) Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("schreg_" + suffix);
        var flowName = "schreg_" + suffix + "_00_sch";
        return (repoId, flowName, "${env:SQLFLOW_SCHREG_" + suffix.ToUpperInvariant() + "}", CatalogIdentity.Pipeline(repoId, flowName));
    }

    private static void AddRepoAndPipeline(CatalogDbContext db, Guid repoId, string flowName, string serverRef, Guid pipelineId)
    {
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = "repo_" + flowName, FirstSeenUtc = now, LastSyncUtc = now });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = pipelineId, RepoId = repoId, Name = flowName, Kind = "sch", RelativePath = flowName + ".yaml",
            Active = true, SourceServer = serverRef, TargetServer = ServerIdentity.FileSystem,
            DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now, Yaml = "flowType: sch\n",
        });
    }

    private static string Artifact(Guid runId, string flowName, params (string Name, string Kind)[] objects)
    {
        var items = string.Join(",", objects.Select(o => o.Kind == "Table"
            ? $$"""
                { "schema": "dbo", "name": "{{o.Name}}", "kind": "Table", "keyColumns": ["Id"],
                  "script": "CREATE TABLE [dbo].[{{o.Name}}] ([Id] int NOT NULL)",
                  "columns": [ { "ordinal": 1, "name": "Id", "dataType": "int", "nullable": false },
                               { "ordinal": 2, "name": "Name", "dataType": "nvarchar(50)", "nullable": true } ] }
                """
            : $$"""
                { "schema": "dbo", "name": "{{o.Name}}", "kind": "View", "keyColumns": [],
                  "script": "SELECT 1 AS Id",
                  "columns": [ { "ordinal": 1, "name": "Id", "dataType": "int", "nullable": false } ] }
                """));
        return $$"""
            {
              "schemaVersion": 1,
              "flowKind": "sch",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": true,
              "writtenUtc": "2026-09-16T10:00:00Z",
              "result": {
                "runId": "{{runId}}",
                "flowName": "{{flowName}}",
                "success": true,
                "database": "Registry",
                "objects": [ {{items}} ],
                "tables": 1,
                "views": 0,
                "columns": 2,
                "warnings": [],
                "startedUtc": "2026-09-16T09:59:59Z",
                "endedUtc": "2026-09-16T10:00:00Z",
                "durationSeconds": 1.0
              }
            }
            """;
    }

    private static async Task ExecAsync(string cs, params string[] statements)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CleanupAsync(string cs, Guid repoId, string serverRef)
    {
        var prefix = serverRef.ToLowerInvariant() + "|";
        await using var db = CatalogDatabase.Create(cs);
        await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
        await db.ObjectColumns.Where(c => c.ObjectKey.StartsWith(prefix)).ExecuteDeleteAsync();
        await db.Objects.Where(o => o.Key.StartsWith(prefix)).ExecuteDeleteAsync();
        await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }
}
