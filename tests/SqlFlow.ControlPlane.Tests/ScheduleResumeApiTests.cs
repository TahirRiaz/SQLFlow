using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Resuming a schedule re-arms its clock from now, and only its clock: a schedule with no computable next fire keeps
/// none. A chained schedule is driven by its parents, so a resume that armed it at "now" let the clock scan claim an
/// occurrence that ran nothing, stamp its last fire, and set off every schedule chained behind it.
/// </summary>
public sealed class ScheduleResumeApiTests
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ResumingAChainedSchedule_LeavesItWithoutAClockFire()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("resume_" + suffix);
        var flowName = "chained_flow_" + suffix;

        // The in-process worker and scheduler stay out of the way: the test reads the row the resume wrote.
        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithCatalog(cs);
        try
        {
            Guid id;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = "resume_" + suffix,
                    RemoteUrl = "https://git.invalid/resume.git",
                    RootPath = Path.Combine(Path.GetTempPath(), "resume_" + suffix),
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, flowName),
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    RelativePath = $"flows/{flowName}.yaml",
                    Active = true,
                    ExecutionMode = PipelineExecutionModes.Auto,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();

                id = await ScheduleStore.UpsertYamlScheduleAsync(
                    db, repoId, "after_parent_" + suffix, [flowName], cron: null, intervalSeconds: null, "UTC",
                    enabled: true, catchup: false, maxConcurrency: null, computedNextFireUtc: now, nowUtc: now,
                    afterSchedules: ["parent_" + suffix]);
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            using (var pause = await PostAsync(client, token, $"/api/v1/schedules/{id}/pause"))
            {
                Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
            }

            using var resume = await PostAsync(client, token, $"/api/v1/schedules/{id}/resume");
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            var resumed = (await resume.Content.ReadFromJsonAsync<ScheduleDto>())!;
            Assert.False(resumed.Paused);
            Assert.Null(resumed.NextFireUtc);
            Assert.Equal(["parent_" + suffix], resumed.AfterSchedules);

            await using var check = CatalogDatabase.Create(cs);
            var row = await check.Schedules.AsNoTracking().SingleAsync(s => s.Id == id);
            Assert.Null(row.NextFireUtc);
            Assert.Null(row.LastFireUtc);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ScheduleParents.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<string> IssueTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
