using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The project-scoped, cross-repo lineage graph end to end against the real shadow catalog. The test seeds two
/// repos wired so a flow in one repo's project ("Boatbooking") writes a table that a flow in a DIFFERENT repo reads
/// and re-publishes, which a third flow reads again. It then asserts that <c>/lineage/projects</c> lists the
/// (repo, project) pairs, that <c>/lineage/project-graph</c> seeded on the project returns the whole downstream
/// closure across the repo boundary, that a hop-depth cap stops the walk and reports the cut object as a frontier,
/// and that seeding on an object (a deep-link) returns its immediate producer and consumer. Every seeded row is
/// removed in a finally so repeated runs stay isolated in a shared catalog.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageProjectGraphApiTests
{
    [SkippableFact]
    public async Task ProjectGraph_ClosesDownstreamAcrossRepos_CapsDepth_AndSeedsFromObject()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_PG_" + suffix + "}";
        var repoA = Guid.NewGuid();
        var repoB = Guid.NewGuid();
        var repoAName = "prod_" + suffix;
        var repoBName = "mart_" + suffix;
        var now = DateTime.UtcNow;

        // The seed project's flow (repo A), two downstream flows in repo B (one at each further hop), and an
        // unrelated flow in repo A that shares no object with the chain, so exclusion is proven.
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p4 = Guid.NewGuid();
        var pOther = Guid.NewGuid();

        var source = "file|baat/" + suffix + "/source.csv";
        var o1 = $"{serverRef}|dw|dbo|boatbooking_trans";
        var o2 = $"{serverRef}|dw|mart|sales";
        var o3 = $"{serverRef}|dw|mart|sales_daily";
        var ox = $"{serverRef}|dw|dbo|unrelated_src";
        var oy = $"{serverRef}|dw|dbo|unrelated_out";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoA, Name = repoAName, FirstSeenUtc = now, LastSyncUtc = now });
                db.Repos.Add(new CatalogRepo { Id = repoB, Name = repoBName, FirstSeenUtc = now, LastSyncUtc = now });

                db.Objects.Add(SeedTable(o1, serverRef, "DW", "dbo", "Boatbooking_trans", now));
                db.Objects.Add(SeedTable(o2, serverRef, "DW", "mart", "Sales", now));
                db.Objects.Add(SeedTable(o3, serverRef, "DW", "mart", "Sales_daily", now));

                db.Pipelines.Add(SeedPipeline(p1, repoA, "BB_Boatbooking_00_cpy", "cpy", "Boatbooking/pre/cpy.flow.yaml", 1, now));
                db.Pipelines.Add(SeedPipeline(p2, repoB, "Sales_load", "ing", "Sales/pre/load.flow.yaml", 1, now));
                db.Pipelines.Add(SeedPipeline(p4, repoB, "Sales_daily_agg", "ing", "Sales/pre/agg.flow.yaml", 2, now));
                db.Pipelines.Add(SeedPipeline(pOther, repoA, "Other_flow", "ing", "Other/pre/x.flow.yaml", 1, now));

                // The chain: P1 reads a source file and writes O1 (repo A) -> P2 reads O1 and writes O2 (repo B) ->
                // P4 reads O2 and writes O3 (repo B). The unrelated flow touches only its own two objects.
                db.LineageEdges.Add(Edge(repoA, p1, "BB_Boatbooking_00_cpy", "Reads", source, "source.csv"));
                db.LineageEdges.Add(Edge(repoA, p1, "BB_Boatbooking_00_cpy", "Writes", o1, "Boatbooking_trans"));
                db.LineageEdges.Add(Edge(repoB, p2, "Sales_load", "Reads", o1, "Boatbooking_trans"));
                db.LineageEdges.Add(Edge(repoB, p2, "Sales_load", "Writes", o2, "Sales"));
                db.LineageEdges.Add(Edge(repoB, p4, "Sales_daily_agg", "Reads", o2, "Sales"));
                db.LineageEdges.Add(Edge(repoB, p4, "Sales_daily_agg", "Writes", o3, "Sales_daily"));
                db.LineageEdges.Add(Edge(repoA, pOther, "Other_flow", "Reads", ox, "unrelated_src"));
                db.LineageEdges.Add(Edge(repoA, pOther, "Other_flow", "Writes", oy, "unrelated_out"));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // The scope picker lists every (repo, project) pair with its active-flow count. Our repos contribute
            // Boatbooking (1), Other (1) in repo A and Sales (2) in repo B.
            var projects = await GetJsonAsync<IReadOnlyList<LineageProjectDto>>(client, token, "/api/v1/lineage/projects");
            Assert.Equal(1, projects.Single(p => p.RepoId == repoA && p.Project == "Boatbooking").FlowCount);
            Assert.Equal(1, projects.Single(p => p.RepoId == repoA && p.Project == "Other").FlowCount);
            Assert.Equal(2, projects.Single(p => p.RepoId == repoB && p.Project == "Sales").FlowCount);

            // Full closure seeded on the project: the seed flow plus every downstream flow, crossing into repo B,
            // and NOT the unrelated flow. The cross-repo nodes carry their own repo name.
            var full = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?repoId={repoA}&project=Boatbooking");
            var ids = full.Pipelines.Select(p => p.Id).ToHashSet();
            Assert.Contains(p1, ids);
            Assert.Contains(p2, ids);
            Assert.Contains(p4, ids);
            Assert.DoesNotContain(pOther, ids);

            var seed = full.Pipelines.Single(p => p.Id == p1);
            Assert.True(seed.IsSeed);
            var downstream = full.Pipelines.Single(p => p.Id == p2);
            Assert.False(downstream.IsSeed);
            Assert.Equal(repoB, downstream.RepoId);
            Assert.Equal(repoBName, downstream.RepoName);

            // The base source object the seed reads and every produced table are present; the unrelated objects are
            // not, since no path reaches them.
            var fullKeys = full.Edges.Select(e => e.ObjectKey).ToHashSet();
            Assert.Contains(source, fullKeys);
            Assert.Contains(o1, fullKeys);
            Assert.Contains(o2, fullKeys);
            Assert.Contains(o3, fullKeys);
            Assert.DoesNotContain(ox, fullKeys);
            Assert.DoesNotContain(oy, fullKeys);
            Assert.Empty(full.Frontier);

            // One hop only: the seed and its immediate consumer, but not the flow two hops down. The object whose
            // downstream was cut is reported as a frontier the client can expand.
            var capped = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?repoId={repoA}&project=Boatbooking&depth=1");
            var cappedIds = capped.Pipelines.Select(p => p.Id).ToHashSet();
            Assert.Contains(p1, cappedIds);
            Assert.Contains(p2, cappedIds);
            Assert.DoesNotContain(p4, cappedIds);
            Assert.Contains(o2, capped.Frontier);

            // Seeding on an object (a deep-link into the graph, no project) returns its immediate producer and
            // consumer so the jumped-to object shows where it comes from and where it goes.
            var fromObject = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?expand={Uri.EscapeDataString(o1)}");
            var objectIds = fromObject.Pipelines.Select(p => p.Id).ToHashSet();
            Assert.Contains(p1, objectIds);
            Assert.Contains(p2, objectIds);

            // A project with no seed and no expand is a bad request, not an empty graph.
            using var noSeed = await SendAsync(client, token, $"/api/v1/lineage/project-graph?repoId={repoA}");
            Assert.Equal(HttpStatusCode.BadRequest, noSeed.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repoA || e.RepoId == repoB).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoA || p.RepoId == repoB).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoA || r.Id == repoB).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// A subscriber's queries are attributed via <c>ViaModule</c>, never <c>PipelineId</c>/<c>ObjectKey</c> (a
    /// subscriber is not a data object and runs no flow), so the graph's expand-by-object-key path must resolve
    /// it differently than a table. Seeds one flow writing a table and one subscriber reading it, then expands
    /// the graph directly on the SUBSCRIBER's node key - exactly what the GUI does when a user searches a
    /// dashboard by name in the lineage graph and clicks the "Subscriber" hit - and asserts the table and its
    /// producing flow come back, instead of the empty "no lineage references this node" result the bug produced.
    /// </summary>
    [SkippableFact]
    public async Task ProjectGraph_ExpandedFromSubscriberKey_ReturnsWhatItReadsAndThatObjectsProducer()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_PG_SUB_" + suffix + "}";
        var repo = Guid.NewGuid();
        var repoName = "dwh_" + suffix;
        var now = DateTime.UtcNow;

        var p1 = Guid.NewGuid();
        var o1 = $"{serverRef}|dw|mart|sales";
        var subscriberName = "Dashboard_" + suffix;
        var subscriberKey = $"subscriber|||{subscriberName.ToLowerInvariant()}";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repo, Name = repoName, FirstSeenUtc = now, LastSyncUtc = now });
                db.Objects.Add(SeedTable(o1, serverRef, "DW", "mart", "Sales", now));
                db.Pipelines.Add(SeedPipeline(p1, repo, "Sales_load", "ing", "Sales/pre/load.flow.yaml", 1, now));
                db.LineageEdges.Add(Edge(repo, p1, "Sales_load", "Writes", o1, "Sales"));

                // The subscriber's own row (what the GUI's search box matches on) plus its Reads edge, attributed
                // via ViaModule with no PipelineId/Flow - the shape CatalogSync actually persists for a
                // subscriber's query.
                db.Subscribers.Add(new CatalogSubscriber
                {
                    RepoId = repo,
                    Name = subscriberName,
                    Type = "PowerBI",
                    ObjectKey = subscriberKey,
                    File = "subscribers/" + suffix + ".subscribers.yaml",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repo,
                    Flow = null,
                    PipelineId = null,
                    ViaModule = subscriberKey,
                    Relation = "Reads",
                    ObjectKey = o1,
                    ObjectName = "Sales",
                    Tier = "Declared",
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var graph = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?expand={Uri.EscapeDataString(subscriberKey)}");

            // Before the fix this came back with no pipelines, no edges, and no objects: expanding an object key
            // looked the subscriber up in dictionaries keyed by ObjectKey, which a subscriber's key never is.
            Assert.Contains(p1, graph.Pipelines.Select(p => p.Id));
            Assert.Contains(o1, graph.Edges.Select(e => e.ObjectKey));
            var subscriberNode = Assert.Single(graph.Objects, obj => obj.Key == subscriberKey);
            Assert.Equal("subscriber", subscriberNode.Kind);
            Assert.Equal(subscriberName, subscriberNode.Name);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repo).ExecuteDeleteAsync();
            await db.Subscribers.Where(s => s.RepoId == repo).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repo).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repo).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// A derived dataset (a dataset a registered flow kind declares as derived from other datasets) sits between its
    /// sources and the flow reading it. The sync stores the dataset's own reads with no pipeline (its node as their
    /// module), the flow's read of the dataset, and what the flow inherits through it; the graph draws each source
    /// into the dataset and the dataset into the flow, and never a second, direct edge from a source to the flow. A
    /// read the flow makes in its own right still draws, the walk reaches the reading flow from the flow writing a
    /// source, and a deep link onto the dataset shows what it is derived from.
    /// </summary>
    [SkippableFact]
    public async Task ProjectGraph_DrawsADerivedDatasetBetweenItsSourcesAndTheFlowReadingIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_PG_DRV_" + suffix + "}";
        var storeRef = "dataset:probe-store-" + suffix;
        var modelRef = "dataset:probe-model-" + suffix;
        var repo = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var writer = Guid.NewGuid();
        var reader = Guid.NewGuid();
        var written = $"{storeRef}|tenant1|master|wks:unit:1.0.0";
        var unwritten = $"{storeRef}|tenant1|master|wks:code:1.0.0";
        var model = $"{modelRef}|tenant1|models|well-model";
        var direct = $"{serverRef}|dw|arc|wells";
        var target = $"{serverRef}|dw|mart|wells_out";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repo, Name = "drv_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Objects.Add(SeedDataset(written, storeRef, "tenant1", "master", "wks:unit:1.0.0", now));
                db.Objects.Add(SeedDataset(unwritten, storeRef, "tenant1", "master", "wks:code:1.0.0", now));
                db.Objects.Add(SeedDataset(model, modelRef, "tenant1", "models", "Well-Model", now));
                db.Objects.Add(SeedTable(direct, serverRef, "DW", "arc", "Wells", now));
                db.Objects.Add(SeedTable(target, serverRef, "DW", "mart", "Wells_out", now));

                db.Pipelines.Add(SeedPipeline(writer, repo, "Units_capture", "probe", "Units/capture.yaml", 1, now));
                db.Pipelines.Add(SeedPipeline(reader, repo, "Wells_render", "probe", "Wells/render.yaml", 2, now));

                db.LineageEdges.Add(Edge(repo, writer, "Units_capture", "Writes", written, "wks:unit:1.0.0"));

                // The dataset's own reads: no pipeline, its node as the module.
                db.LineageEdges.Add(ModuleEdge(repo, model, written, "wks:unit:1.0.0"));
                db.LineageEdges.Add(ModuleEdge(repo, model, unwritten, "wks:code:1.0.0"));

                // The flow reads the dataset and a table of its own, inherits the dataset's sources, and writes a table.
                db.LineageEdges.Add(Edge(repo, reader, "Wells_render", "Reads", model, "Well-Model"));
                db.LineageEdges.Add(Edge(repo, reader, "Wells_render", "Reads", direct, "Wells"));
                db.LineageEdges.Add(Inherited(repo, reader, "Wells_render", model, written, "wks:unit:1.0.0"));
                db.LineageEdges.Add(Inherited(repo, reader, "Wells_render", model, unwritten, "wks:code:1.0.0"));
                db.LineageEdges.Add(Edge(repo, reader, "Wells_render", "Writes", target, "Wells_out"));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // Seeded on the project of the flow writing a source: the walk reaches the reading flow through what it
            // inherits, so the whole chain is one graph.
            var graph = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?repoId={repo}&project=Units");
            Assert.Equal([writer, reader], graph.Pipelines.OrderBy(p => p.Depth).Select(p => p.Id).ToList());

            var node = Assert.Single(graph.Objects, o => o.Key == model);
            Assert.Equal(("Well-Model", "probe model " + suffix, "tenant1.models"), (node.Name, node.Kind, node.Location));

            var flows = graph.FlowGraph.Select(e => (e.Source, e.Target, e.Label, e.PipelineId)).ToHashSet();
            Assert.Contains((writer.ToString(), written, "writes", (Guid?)writer), flows);
            Assert.Contains((written, model, "reads", (Guid?)null), flows);
            Assert.Contains((unwritten, model, "reads", (Guid?)null), flows);
            Assert.Contains((model, reader.ToString(), "reads", (Guid?)reader), flows);
            Assert.Contains((direct, reader.ToString(), "reads", (Guid?)reader), flows);
            Assert.Contains((reader.ToString(), target, "writes", (Guid?)reader), flows);
            Assert.DoesNotContain(graph.FlowGraph, e => (e.Source == written || e.Source == unwritten) && e.Target == reader.ToString());
            Assert.Equal(6, flows.Count);

            var objects = graph.ObjectGraph.Select(e => (e.Source, e.Target, e.Label, e.PipelineId)).ToHashSet();
            Assert.Contains((written, model, "reads", (Guid?)null), objects);
            Assert.Contains((unwritten, model, "reads", (Guid?)null), objects);
            Assert.Contains((model, target, "Wells_render", (Guid?)reader), objects);
            Assert.Contains((direct, target, "Wells_render", (Guid?)reader), objects);
            Assert.DoesNotContain(graph.ObjectGraph, e => (e.Source == written || e.Source == unwritten) && e.Target == target);
            Assert.Equal(4, objects.Count);

            // A deep link onto the dataset itself: the flow reading it, and what it is derived from.
            var fromModel = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?expand={Uri.EscapeDataString(model)}");
            Assert.Contains(reader, fromModel.Pipelines.Select(p => p.Id));
            Assert.Contains(fromModel.FlowGraph, e => e.Source == written && e.Target == model && e.Label == "reads");
            Assert.Contains(fromModel.FlowGraph, e => e.Source == model && e.Target == reader.ToString());
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repo).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repo).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef || o.ServerRef == storeRef || o.ServerRef == modelRef).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repo).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// A view sits between the tables its body reads and the flow reading it. The sync stores the view's own reads with
    /// no pipeline (the view as their module), the flow's read of the view, and the base tables the flow inherits
    /// through it; the graph draws each base table into the view and the view into the flow, and never a second, direct
    /// edge from a base table to the flow. A read the flow makes of a base table in its own right still draws.
    /// </summary>
    [SkippableFact]
    public async Task ProjectGraph_DrawsAViewBetweenItsBaseTablesAndTheFlowReadingIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_PG_VIEW_" + suffix + "}";
        var repo = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var writer = Guid.NewGuid();
        var reader = Guid.NewGuid();
        var inherited = $"{serverRef}|dw|dim|well";
        var alsoDirect = $"{serverRef}|dw|dim|wellbore";
        var view = $"{serverRef}|dw|dim|well_wellbore";
        var target = $"{serverRef}|dw|mart|wells_out";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repo, Name = "view_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Objects.Add(SeedTable(inherited, serverRef, "DW", "dim", "Well", now));
                db.Objects.Add(SeedTable(alsoDirect, serverRef, "DW", "dim", "Wellbore", now));
                db.Objects.Add(SeedTable(view, serverRef, "DW", "dim", "Well_wellbore", now, kind: "View"));
                db.Objects.Add(SeedTable(target, serverRef, "DW", "mart", "Wells_out", now));

                db.Pipelines.Add(SeedPipeline(writer, repo, "Dims_build", "ing", "Dims/build.yaml", 1, now));
                db.Pipelines.Add(SeedPipeline(reader, repo, "Wells_render", "ing", "Wells/render.yaml", 2, now));

                // One flow writes both tables and the view over them.
                db.LineageEdges.Add(Edge(repo, writer, "Dims_build", "Writes", inherited, "Well"));
                db.LineageEdges.Add(Edge(repo, writer, "Dims_build", "Writes", alsoDirect, "Wellbore"));
                db.LineageEdges.Add(Edge(repo, writer, "Dims_build", "Writes", view, "Well_wellbore"));

                // The view's own reads: no pipeline, the view as the module.
                db.LineageEdges.Add(ModuleEdge(repo, view, inherited, "Well"));
                db.LineageEdges.Add(ModuleEdge(repo, view, alsoDirect, "Wellbore"));

                // The flow reads the view and one of its tables in its own right, inherits both tables through the
                // view, and writes a table.
                db.LineageEdges.Add(Edge(repo, reader, "Wells_render", "Reads", view, "Well_wellbore"));
                db.LineageEdges.Add(Edge(repo, reader, "Wells_render", "Reads", alsoDirect, "Wellbore"));
                db.LineageEdges.Add(Inherited(repo, reader, "Wells_render", view, inherited, "Well"));
                db.LineageEdges.Add(Inherited(repo, reader, "Wells_render", view, alsoDirect, "Wellbore"));
                db.LineageEdges.Add(Edge(repo, reader, "Wells_render", "Writes", target, "Wells_out"));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var graph = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?repoId={repo}&project=Dims");
            Assert.Equal([writer, reader], graph.Pipelines.OrderBy(p => p.Depth).Select(p => p.Id).ToList());
            Assert.Equal("view", Assert.Single(graph.Objects, o => o.Key == view).Kind);

            var flows = graph.FlowGraph.Select(e => (e.Source, e.Target, e.Label, e.PipelineId)).ToHashSet();
            Assert.Contains((writer.ToString(), inherited, "writes", (Guid?)writer), flows);
            Assert.Contains((writer.ToString(), alsoDirect, "writes", (Guid?)writer), flows);
            Assert.Contains((inherited, view, "view", (Guid?)writer), flows);
            Assert.Contains((alsoDirect, view, "view", (Guid?)writer), flows);
            Assert.Contains((view, reader.ToString(), "reads", (Guid?)reader), flows);
            Assert.Contains((alsoDirect, reader.ToString(), "reads", (Guid?)reader), flows);
            Assert.Contains((reader.ToString(), target, "writes", (Guid?)reader), flows);
            Assert.DoesNotContain(graph.FlowGraph, e => e.Source == inherited && e.Target == reader.ToString());
            Assert.Equal(7, flows.Count);

            var objects = graph.ObjectGraph.Select(e => (e.Source, e.Target, e.Label, e.PipelineId)).ToHashSet();
            Assert.Contains((inherited, view, "Dims_build", (Guid?)writer), objects);
            Assert.Contains((alsoDirect, view, "Dims_build", (Guid?)writer), objects);
            Assert.Contains((view, target, "Wells_render", (Guid?)reader), objects);
            Assert.Contains((alsoDirect, target, "Wells_render", (Guid?)reader), objects);
            Assert.DoesNotContain(graph.ObjectGraph, e => e.Source == inherited && e.Target == target);
            Assert.Equal(4, objects.Count);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repo).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repo).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repo).ExecuteDeleteAsync();
        }
    }

    private static CatalogObject SeedDataset(
        string key, string serverRef, string ns, string group, string name, DateTime now)
        => new()
        {
            Key = key,
            ServerRef = serverRef,
            Database = ns,
            Schema = group,
            Name = name,
            Kind = "Dataset",
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    /// <summary>A read a module (a view, or a derived dataset) makes of one of its sources: no pipeline, the module's node as the module.</summary>
    private static CatalogLineageEdge ModuleEdge(Guid repoId, string moduleKey, string objectKey, string objectName)
        => new()
        {
            RepoId = repoId,
            Flow = null,
            PipelineId = null,
            ViaModule = moduleKey,
            Relation = "Reads",
            ObjectKey = objectKey,
            ObjectName = objectName,
            Tier = "Declared",
        };

    /// <summary>A read a flow inherits through a module it reads, as the graph builder attributes it to the flow.</summary>
    private static CatalogLineageEdge Inherited(
        Guid repoId, Guid pipelineId, string flow, string moduleKey, string objectKey, string objectName)
        => new()
        {
            RepoId = repoId,
            PipelineId = pipelineId,
            Flow = flow,
            ViaModule = moduleKey,
            Relation = "Reads",
            ObjectKey = objectKey,
            ObjectName = objectName,
            Tier = "Derived",
        };

    private static CatalogObject SeedTable(
        string key, string serverRef, string database, string schema, string name, DateTime now, string kind = "Table")
        => new()
        {
            Key = key,
            ServerRef = serverRef,
            Database = database,
            Schema = schema,
            Name = name,
            Kind = kind,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogPipeline SeedPipeline(
        Guid id, Guid repoId, string name, string kind, string relativePath, int wave, DateTime now)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            Kind = kind,
            RelativePath = relativePath,
            Active = true,
            Wave = wave,
            DefinitionJson = "{}",
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogLineageEdge Edge(
        Guid repoId, Guid pipelineId, string flow, string relation, string objectKey, string objectName)
        => new()
        {
            RepoId = repoId,
            PipelineId = pipelineId,
            Flow = flow,
            Relation = relation,
            ObjectKey = objectKey,
            ObjectName = objectName,
            Tier = "Declared",
        };

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
