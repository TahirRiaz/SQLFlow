using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What started a run, recorded at enqueue and read back through the API against the real catalog and the real
/// scheduler: a person's run-now of a schedule is recorded as asked for and names them, the schedule chained behind it
/// fires on its own and records the parent fire it followed, and a run's page walks that chain back to the person. The
/// runs list's "Started by" filter keeps exactly the runs each label names. Seeded rows are removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunStartedByApiTests
{
    [SkippableFact]
    public async Task A_chained_fire_records_the_parent_fire_it_followed_and_its_page_walks_back_to_the_person()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("started_" + suffix);
        string first = $"started_a_{suffix}", second = $"started_b_{suffix}", child = $"started_c_{suffix}";
        string parentName = $"land_{suffix}", childName = $"model_{suffix}";
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                SeedRepo(db, repoId);
                foreach (var flow in new[] { first, second, child })
                {
                    SeedPipeline(db, repoId, flow);
                }

                // The parent has a clock it will not reach during the test, so only the run-now fires it; the child has no
                // clock at all and fires behind the parent.
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = parentId, RepoId = repoId, Name = parentName, Cron = "0 6 1 1 *", Timezone = "UTC", Enabled = true,
                    Source = "api", NextFireUtc = DateTime.UtcNow.AddYears(1), CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                });
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = childId, RepoId = repoId, Name = childName, Timezone = "UTC", Enabled = true, Source = "yaml",
                    CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                    Parents = [new CatalogScheduleParent { ScheduleId = childId, RepoId = repoId, ParentName = parentName, Ordinal = 0 }],
                });
                db.ScheduleMembers.Add(Member(parentId, repoId, first));
                db.ScheduleMembers.Add(Member(parentId, repoId, second));
                db.ScheduleMembers.Add(Member(childId, repoId, child));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var alice = await IssueTokenAsync(client, "alice", ["operate"]);

            ScheduleRunAccepted accepted;
            using (var run = await SendAsync(client, alice, HttpMethod.Post, $"/api/v1/schedules/{parentId}/run"))
            {
                Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
                accepted = (await run.Content.ReadFromJsonAsync<ScheduleRunAccepted>())!;
            }

            Assert.NotNull(accepted.GroupId);

            // The node runs the parent's two members (each fails at once: their flows declare nothing to read), which ends
            // the parent's fire, and the scheduler then fires the child behind it.
            CatalogRun? childRun = null;
            for (var attempt = 0; attempt < 240 && childRun is null; attempt++)
            {
                await Task.Delay(250);
                await using var db = CatalogDatabase.Create(cs);
                childRun = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RepoId == repoId && r.FlowName == child);
            }

            Assert.NotNull(childRun);
            Assert.Equal(RunTriggerSources.Chain, childRun.TriggerSource);
            Assert.Equal(childId, childRun.TriggerScheduleId);
            Assert.Equal($"schedule:{childName}", childRun.RequestedBy);
            Assert.Equal(accepted.RunId, childRun.TriggerRunId);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var parentRuns = await db.Runs.AsNoTracking().Where(r => r.GroupId == accepted.GroupId).ToListAsync();
                Assert.Equal(2, parentRuns.Count);
                Assert.All(parentRuns, r =>
                {
                    Assert.Equal(RunTriggerSources.Manual, r.TriggerSource);
                    Assert.Equal("alice", r.RequestedBy);
                    Assert.Equal(parentId, r.TriggerScheduleId);
                    Assert.Null(r.TriggerRunId);
                });
            }

            var reader = await IssueTokenAsync(client, "reader", ["read"]);

            // The child's page says it fired behind the parent, and walks the chain back to alice's run-now.
            var detail = await GetJsonAsync<RunDetailDto>(client, reader, $"/api/v1/runs/{childRun.RunId}");
            Assert.NotNull(detail.StartedBy);
            Assert.Equal(RunStartedByKinds.Chain, detail.StartedBy.Kind);
            Assert.Equal($"Schedule {childName} fired after schedule {parentName} finished.", detail.StartedBy.Summary);
            Assert.Equal(accepted.RunId, detail.StartedBy.AfterRunId);
            Assert.Equal(parentName, detail.StartedBy.AfterScheduleName);
            var cause = Assert.Single(detail.Causes!);
            Assert.Equal(accepted.RunId, cause.RunId);
            Assert.Equal(accepted.GroupId, cause.GroupId);
            Assert.Equal(RunStartedByKinds.User, cause.StartedBy.Kind);
            Assert.Equal("alice", cause.StartedBy.Who);
            Assert.Equal($"alice ran schedule {parentName} now.", cause.StartedBy.Summary);

            // The parent's group says the same of all its members.
            var group = await GetJsonAsync<RunGroupDto>(client, reader, $"/api/v1/runs/groups/{accepted.GroupId}");
            Assert.NotNull(group.StartedBy);
            Assert.Equal(RunStartedByKinds.User, group.StartedBy.Kind);
            Assert.Equal(parentId, group.StartedBy.ScheduleId);

            // The board lists each run with what started it, and filters by it.
            var all = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, reader, $"/api/v1/runs?repoId={repoId}");
            Assert.Equal(3, all.Items.Count);
            Assert.All(all.Items, r => Assert.NotNull(r.StartedBy));
            var chained = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, reader, $"/api/v1/runs?repoId={repoId}&startedBy=chain");
            Assert.Equal(childRun.RunId, Assert.Single(chained.Items).RunId);
            var asked = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, reader, $"/api/v1/runs?repoId={repoId}&startedBy=user");
            Assert.Equal(2, asked.Items.Count);
            Assert.All(asked.Items, r => Assert.Equal("alice", r.StartedBy!.Who));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task The_started_by_filter_keeps_exactly_the_runs_each_label_names()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("startedby_" + suffix);
        var flow = $"startedby_flow_{suffix}";

        // Every shape a run can have recorded, with the kind it must be labelled and filtered as.
        (string? Source, string? RequestedBy, string Kind)[] shapes =
        [
            (RunTriggerSources.Schedule, "schedule:nightly", RunStartedByKinds.Schedule),
            (RunTriggerSources.Schedule, null, RunStartedByKinds.Schedule),
            (RunTriggerSources.Chain, "schedule:model", RunStartedByKinds.Chain),
            (RunTriggerSources.Manual, "alice", RunStartedByKinds.User),
            (RunTriggerSources.Manual, null, RunStartedByKinds.User),
            (RunTriggerSources.Schedule, "bob", RunStartedByKinds.User),
            (RunTriggerSources.Manual, "trigger:merge-to-main", RunStartedByKinds.Trigger),
            (RunTriggerSources.Cli, null, RunStartedByKinds.Cli),
            (null, null, RunStartedByKinds.Unknown),
        ];
        var expected = new Dictionary<Guid, string>();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                SeedRepo(db, repoId);
                SeedPipeline(db, repoId, flow);
                var at = DateTime.UtcNow.AddDays(-1);
                foreach (var (source, requestedBy, kind) in shapes)
                {
                    var runId = Guid.CreateVersion7();
                    expected[runId] = kind;
                    db.Runs.Add(new CatalogRun
                    {
                        RunId = runId, PipelineId = CatalogIdentity.Pipeline(repoId, flow), RepoId = repoId, FlowName = flow,
                        FlowKind = "file", Status = RunStatuses.Succeeded, Success = true, SchemaVersion = 1,
                        EnqueuedUtc = at, WrittenUtc = at, TriggerSource = source, RequestedBy = requestedBy,
                    });
                }

                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var reader = await IssueTokenAsync(client, "reader", ["read"]);

            var all = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, reader, $"/api/v1/runs?repoId={repoId}");
            Assert.Equal(expected.Count, all.Items.Count);
            Assert.All(all.Items, r => Assert.Equal(expected[r.RunId], r.StartedBy!.Kind));

            foreach (var kind in RunStartedByKinds.All)
            {
                var filtered = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                    client, reader, $"/api/v1/runs?repoId={repoId}&startedBy={kind}");
                Assert.Equal(
                    expected.Where(e => e.Value == kind).Select(e => e.Key).Order().ToList(),
                    filtered.Items.Select(r => r.RunId).Order().ToList());
            }

            using var bad = await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/runs?repoId={repoId}&startedBy=robot");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static void SeedRepo(CatalogDbContext db, Guid repoId) => db.Repos.Add(new CatalogRepo
    {
        Id = repoId,
        Name = "started_repo_" + repoId.ToString("N")[..8],
        RootPath = Path.Combine(Path.GetTempPath(), "started_" + repoId.ToString("N")[..8]),
        FirstSeenUtc = DateTime.UtcNow,
        LastSyncUtc = DateTime.UtcNow,
    });

    private static void SeedPipeline(CatalogDbContext db, Guid repoId, string flowName) => db.Pipelines.Add(new CatalogPipeline
    {
        Id = CatalogIdentity.Pipeline(repoId, flowName),
        RepoId = repoId,
        Name = flowName,
        Kind = "file",
        RelativePath = $"flows/{flowName}.flow.yaml",
        ContentHash = new string('0', 64),
        Yaml = $"name: {flowName}\nflowType: file\n",
        DefinitionJson = $$"""{"name":"{{flowName}}","flowType":"file"}""",
        Active = true,
        Wave = 0,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
    });

    private static CatalogScheduleMember Member(Guid scheduleId, Guid repoId, string flowName) => new()
    {
        ScheduleId = scheduleId,
        PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
        RepoId = repoId,
        FlowName = flowName,
    };

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleParents.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, string subject, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, subject, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string relativeUri)
    {
        using var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
