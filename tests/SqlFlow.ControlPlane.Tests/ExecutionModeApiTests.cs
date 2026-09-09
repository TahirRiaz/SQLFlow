using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The execution-mode surface of the control plane: the queue persists the assertions-only flag on the run row,
/// the trigger API accepts it for an ingestion flow and refuses it for any other kind (and for a group scope),
/// and a <c>mode: manual</c> pipeline is excluded from batch/node scope expansion while a direct anchor still
/// runs. Gated on a reachable catalog database, like the other DB-backed control-plane suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExecutionModeApiTests
{
    [SkippableFact]
    public async Task Enqueue_PersistsAssertionsOnly_OnTheRunRow()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var flowName = $"ao-queue-{Guid.NewGuid():N}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(
                db,
                new RunEnqueueRequest(repoId, flowName, "ing", Parameters: new RunParameters { AssertionsOnly = true }),
                DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.True(run.AssertionsOnly);
            Assert.False(run.FullLoad);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task TriggerApi_AcceptsAssertionsOnlyForIngestion_AndRefusesOtherKindsAndGroupScopes()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = $"ao-api-{Guid.NewGuid():N}";
        var repoId = SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = name, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(Pipeline(repoId, "flow-ing", "ing", now));
                db.Pipelines.Add(Pipeline(repoId, "flow-hc", "hc", now));
                await db.SaveChangesAsync();
            }

            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // Assertions are an ingestion concept: an hc flow is refused at the boundary.
            using var wrongKind = await SendAsync(client, token, new { repoId, flowName = "flow-hc", assertionsOnly = true });
            Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);

            // An assertions-only run is a single-flow concept: a group (node) scope is refused at the boundary.
            using var groupScope = await SendAsync(client, token, new { repoId, flowName = "flow-ing", scope = "node", assertionsOnly = true });
            Assert.Equal(HttpStatusCode.BadRequest, groupScope.StatusCode);

            // The ingestion flow is accepted, and the run detail carries the flag for the audit trail.
            using var accepted = await SendAsync(client, token, new { repoId, flowName = "flow-ing", pool = "ao-unserved", assertionsOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var run = await accepted.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(run);

            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{run.RunId}", UriKind.Relative));
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var detail = await client.SendAsync(detailRequest);
            var body = await detail.Content.ReadAsStringAsync();
            Assert.Contains("\"assertionsOnly\":true", body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ScopeExpansion_ExcludesManualPipelines_ExceptTheNodeAnchor()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = $"mode-scope-{Guid.NewGuid():N}";
        var repoId = SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = name, FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(Pipeline(repoId, "load-orders", "ing", now, batch: "BB"));
            db.Pipelines.Add(Pipeline(repoId, "watch-orders", "hc", now, batch: "BB"));
            db.Pipelines.Add(Pipeline(repoId, "watch-revenue", "hc", now, batch: "BB", executionMode: PipelineExecutionModes.Manual));
            // load-orders feeds both health checks, so a node run anchored on it reaches them as descendants.
            db.FlowDependencies.Add(Dependency(repoId, "load-orders", "watch-orders"));
            db.FlowDependencies.Add(Dependency(repoId, "load-orders", "watch-revenue"));
            await db.SaveChangesAsync();

            // A schedule all three joined: the manual health check never runs on a fire, because mode: manual
            // reserves a flow for a direct trigger and firing a schedule it sits in is not that.
            var scheduleId = Guid.CreateVersion7();
            db.Schedules.Add(new CatalogSchedule
            {
                Id = scheduleId, RepoId = repoId, Name = "nightly", Cron = "0 4 * * *", Timezone = "UTC",
                Enabled = true, Source = "yaml", CreatedUtc = now, UpdatedUtc = now,
            });
            foreach (var member in new[] { "load-orders", "watch-orders", "watch-revenue" })
            {
                db.ScheduleMembers.Add(new CatalogScheduleMember
                {
                    ScheduleId = scheduleId, PipelineId = CatalogIdentity.Pipeline(repoId, member),
                    RepoId = repoId, FlowName = member,
                });
            }

            await db.SaveChangesAsync();

            var fired = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, "nightly");
            Assert.Equal(["load-orders", "watch-orders"], fired.Members.Select(m => m.FlowName).Order().ToArray());

            // Node scope: the manual descendant is excluded too.
            var node = await RunScopeExpander.ExpandAsync(db, repoId, "load-orders", RunScope.Node);
            Assert.Equal(["load-orders", "watch-orders"], node.Members.Select(m => m.FlowName).Order().ToArray());

            // A manual anchor is kept: naming it directly IS the manual trigger.
            var manualAnchor = await RunScopeExpander.ExpandAsync(db, repoId, "watch-revenue", RunScope.Node);
            Assert.Equal("watch-revenue", Assert.Single(manualAnchor.Members).FlowName);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ScopeExpansion_ExcludesDisabledPipelines_UnlessFindAllIsRequested()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = $"mode-disabled-{Guid.NewGuid():N}";
        var repoId = SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = name, FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(Pipeline(repoId, "acquire", "api", now, batch: "SRC"));
            db.Pipelines.Add(Pipeline(repoId, "land-live", "file", now, batch: "SRC"));
            db.Pipelines.Add(Pipeline(repoId, "merge-retired", "ing", now, batch: "SRC", executionMode: PipelineExecutionModes.Disabled));
            db.FlowDependencies.Add(Dependency(repoId, "acquire", "land-live"));
            db.FlowDependencies.Add(Dependency(repoId, "land-live", "merge-retired"));
            await db.SaveChangesAsync();

            // Find only active (the default): the deactivated descendant is left out of the group.
            var node = await RunScopeExpander.ExpandAsync(db, repoId, "acquire", RunScope.Node);
            Assert.Equal(["acquire", "land-live"], node.Members.Select(m => m.FlowName).Order().ToArray());

            // Find all: the operator deliberately replays the retired branch with its parent.
            var all = await RunScopeExpander.ExpandAsync(db, repoId, "acquire", RunScope.Node, includeAll: true);
            Assert.Equal(["acquire", "land-live", "merge-retired"], all.Members.Select(m => m.FlowName).Order().ToArray());

            // A disabled anchor is kept: naming it directly IS the manual trigger.
            var disabledAnchor = await RunScopeExpander.ExpandAsync(db, repoId, "merge-retired", RunScope.Node);
            Assert.Equal("merge-retired", Assert.Single(disabledAnchor.Members).FlowName);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    private static CatalogPipeline Pipeline(
        Guid repoId, string name, string kind, DateTime now, string? batch = null, string executionMode = PipelineExecutionModes.Auto)
        => new()
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = kind,
            Batch = batch,
            ExecutionMode = executionMode,
            RelativePath = $"{name}.flow.yaml",
            Active = true,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogFlowDependency Dependency(Guid repoId, string fromFlow, string toFlow)
        => new()
        {
            RepoId = repoId,
            FromPipelineId = CatalogIdentity.Pipeline(repoId, fromFlow),
            ToPipelineId = CatalogIdentity.Pipeline(repoId, toFlow),
            FromFlow = fromFlow,
            ToFlow = toFlow,
        };

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/runs", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }
}
