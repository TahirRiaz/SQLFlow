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
/// How long runs take, read through the API at every level against the real shadow catalog: a run summary carries when
/// the run started and ended, a run group says when it ran and how long it took from its first start to its last end, a
/// schedule says the same of its last fire, and a pipeline profiles how long its runs take. Every instant is anchored to
/// a fixed time, so no assertion depends on the wall clock. Seeded rows are removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunTimingApiTests
{
    private static readonly DateTime Anchor = new(2026, 6, 19, 9, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task ARunGroup_TookFromItsFirstMembersStartToItsLastMembersEnd()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds("grp");
        var groupId = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var last = Guid.CreateVersion7();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                SeedRepoAndPipeline(db, repoId, flowName);
                db.RunGroups.Add(new CatalogRunGroup
                {
                    GroupId = groupId,
                    RepoId = repoId,
                    Mode = RunGroupModes.Batch,
                    Anchor = flowName,
                    MemberCount = 3,
                    EnqueuedUtc = Anchor,
                });
                // Two members ran side by side, a minute and two minutes after the fire; a third was skipped, so it never
                // started. The group ran for nine minutes, not for the sum of its members' durations.
                db.Runs.Add(Run(first, repoId, flowName, RunStatuses.Succeeded, Anchor.AddMinutes(1), Anchor.AddMinutes(3), groupId));
                db.Runs.Add(Run(last, repoId, flowName, RunStatuses.Succeeded, Anchor.AddMinutes(2), Anchor.AddMinutes(10), groupId));
                db.Runs.Add(Run(Guid.CreateVersion7(), repoId, flowName, RunStatuses.Skipped, null, Anchor.AddMinutes(4), groupId));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var group = await GetJsonAsync<RunGroupDto>(client, token, $"/api/v1/runs/groups/{groupId}");
            Assert.Equal(Anchor.AddMinutes(1), group.StartedUtc);
            Assert.Equal(Anchor.AddMinutes(10), group.EndedUtc);
            Assert.Equal(540, group.DurationSeconds);

            // Each member summary carries its own start and end, so a list can time a set or a running run itself.
            var members = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, token, $"/api/v1/runs?groupId={groupId}");
            var firstMember = Assert.Single(members.Items, m => m.RunId == first);
            Assert.Equal(Anchor.AddMinutes(1), firstMember.StartUtc);
            Assert.Equal(Anchor.AddMinutes(3), firstMember.EndUtc);

            // While a member still executes the group has started but not ended: the latest end so far is a member's.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == last).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatuses.Running)
                    .SetProperty(r => r.EndUtc, (DateTime?)null)
                    .SetProperty(r => r.DurationSeconds, (double?)null));
            }

            var running = await GetJsonAsync<RunGroupDto>(client, token, $"/api/v1/runs/groups/{groupId}");
            Assert.Equal(Anchor.AddMinutes(1), running.StartedUtc);
            Assert.Null(running.EndedUtc);
            Assert.Null(running.DurationSeconds);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task AScheduleList_SaysWhenTheLastFireRanAndHowLongItTook()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds("sch");
        var groupId = Guid.CreateVersion7();
        var groupFire = Guid.NewGuid();
        var singleFire = Guid.NewGuid();
        var singleRun = Guid.CreateVersion7();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                SeedRepoAndPipeline(db, repoId, flowName);
                db.Schedules.Add(Schedule(groupFire, repoId, flowName + "_group", lastRunId: null, lastGroupId: groupId));
                db.Schedules.Add(Schedule(singleFire, repoId, flowName + "_single", lastRunId: singleRun, lastGroupId: null));
                db.Runs.Add(Run(Guid.CreateVersion7(), repoId, flowName, RunStatuses.Succeeded, Anchor.AddMinutes(1), Anchor.AddMinutes(4), groupId));
                db.Runs.Add(Run(Guid.CreateVersion7(), repoId, flowName, RunStatuses.Failed, Anchor.AddMinutes(4), Anchor.AddMinutes(6), groupId));
                // A single-member fire reports its run's own duration, which the run's kind measured itself.
                var single = Run(singleRun, repoId, flowName, RunStatuses.Succeeded, Anchor, Anchor.AddSeconds(80), groupId: null);
                single.DurationSeconds = 78.5;
                db.Runs.Add(single);
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var list = await GetJsonAsync<PagedResult<ScheduleDto>>(client, token, $"/api/v1/schedules?repoId={repoId}");
            var fired = Assert.Single(list.Items, s => s.Id == groupFire);
            Assert.Equal(Anchor.AddMinutes(1), fired.LastStartedUtc);
            Assert.Equal(Anchor.AddMinutes(6), fired.LastEndedUtc);
            Assert.Equal(300, fired.LastDurationSeconds);

            var one = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{singleFire}");
            Assert.Equal(Anchor, one.LastStartedUtc);
            Assert.Equal(Anchor.AddSeconds(80), one.LastEndedUtc);
            Assert.Equal(78.5, one.LastDurationSeconds);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task APipeline_ProfilesItsNewestSucceededRuns_ItsLastRun_AndTheRunExecutingNow()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds("pip");
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var lastFinished = Guid.CreateVersion7();
        var executing = Guid.CreateVersion7();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                SeedRepoAndPipeline(db, repoId, flowName);
                var hour = 0;
                CatalogRun Timed(Guid runId, string status, double seconds)
                {
                    var start = Anchor.AddHours(hour++);
                    return Run(runId, repoId, flowName, status, start, start.AddSeconds(seconds), groupId: null);
                }

                foreach (var seconds in new[] { 10.0, 30.0, 20.0, 100.0 })
                {
                    db.Runs.Add(Timed(Guid.CreateVersion7(), RunStatuses.Succeeded, seconds));
                }

                // A failed run says when it failed, not how long the work takes: it is the last run, but not typical.
                db.Runs.Add(Timed(lastFinished, RunStatuses.Failed, 5));
                // A fan-out member is a part of its root's run, so it is neither typical nor the last run.
                var member = Timed(Guid.CreateVersion7(), RunStatuses.Succeeded, 1000);
                member.FanOutRoot = lastFinished;
                db.Runs.Add(member);
                db.Runs.Add(Run(executing, repoId, flowName, RunStatuses.Running, Anchor.AddHours(hour), null, groupId: null));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var profile = await GetJsonAsync<PipelineRunDurationsDto>(
                client, token, $"/api/v1/pipelines/{pipelineId}/runs/durations");
            Assert.Equal(4, profile.Runs);
            Assert.Equal((25.0, 40.0, 10.0, 100.0),
                (profile.MedianSeconds!.Value, profile.AverageSeconds!.Value, profile.FastestSeconds!.Value, profile.SlowestSeconds!.Value));
            Assert.NotNull(profile.Last);
            Assert.Equal((lastFinished, RunStatuses.Failed, 5.0), (profile.Last.RunId, profile.Last.Status, profile.Last.DurationSeconds!.Value));
            Assert.NotNull(profile.Running);
            Assert.Equal(executing, profile.Running.RunId);
            Assert.Equal(Anchor.AddHours(6), profile.Running.StartUtc);

            using var unknown = await GetAsync(client, token, $"/api/v1/pipelines/{Guid.NewGuid()}/runs/durations");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static (Guid RepoId, string FlowName) NewIds(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName($"timing_{prefix}_{suffix}"), $"timing_{prefix}_flow_{suffix}");
    }

    private static void SeedRepoAndPipeline(CatalogDbContext db, Guid repoId, string flowName)
    {
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "timing_repo_" + repoId.ToString("N")[..8],
            RootPath = "/tmp/timing_" + repoId.ToString("N")[..8],
            FirstSeenUtc = Anchor,
            LastSyncUtc = Anchor,
        });
        db.Pipelines.Add(new CatalogPipeline
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
            FirstSeenUtc = Anchor,
            LastSeenUtc = Anchor,
        });
    }

    /// <summary>A run of the pipeline in <paramref name="status"/>, started and ended as given; one that ran to an end is
    /// timed by its instants, as the queue records it.</summary>
    private static CatalogRun Run(
        Guid runId, Guid repoId, string flowName, string status, DateTime? startUtc, DateTime? endUtc, Guid? groupId)
        => new()
        {
            RunId = runId,
            PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "file",
            GroupId = groupId,
            Status = status,
            Success = status == RunStatuses.Succeeded,
            SchemaVersion = 1,
            EnqueuedUtc = Anchor,
            StartUtc = startUtc,
            EndUtc = endUtc,
            DurationSeconds = RunElapsed.Seconds(startUtc, endUtc),
            WrittenUtc = endUtc ?? startUtc ?? Anchor,
        };

    private static CatalogSchedule Schedule(Guid id, Guid repoId, string name, Guid? lastRunId, Guid? lastGroupId)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            // Far in the future, so the scheduler cannot fire it mid-test and rewrite the last-fire pointers.
            Cron = "0 6 1 1 *",
            Timezone = "UTC",
            Enabled = true,
            Source = "api",
            NextFireUtc = DateTime.UtcNow.AddYears(1),
            LastFireUtc = Anchor,
            LastRunId = lastRunId,
            LastGroupId = lastGroupId,
            CreatedUtc = Anchor,
            UpdatedUtc = Anchor,
        };

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

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

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await GetAsync(client, token, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
