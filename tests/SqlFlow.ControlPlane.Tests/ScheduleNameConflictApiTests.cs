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
/// Creating a schedule through the API under a name the repo already holds is a 409 naming the schedule that holds it,
/// decided by the store under the repo's schedule-name lock (the one a catalog sync takes), so the check and the insert
/// are one step and a name taken in between is never a 500 from the unique index. Names compare as the index does,
/// so a different spelling of a git schedule's name is refused too.
/// </summary>
public sealed class ScheduleNameConflictApiTests
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CreatingAScheduleUnderAGitSchedulesName_IsAConflictNamingIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("nameclash_" + suffix);
        var flowName = "orders_" + suffix;
        var scheduleName = "nightly_" + suffix;

        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithCatalog(cs);
        try
        {
            Guid yamlId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = "nameclash_" + suffix,
                    RemoteUrl = "https://git.invalid/nameclash.git",
                    RootPath = Path.Combine(Path.GetTempPath(), "nameclash_" + suffix),
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
                yamlId = await ScheduleStore.UpsertYamlScheduleAsync(
                    db, repoId, scheduleName, [flowName], "0 2 * * *", null, "UTC", enabled: true, catchup: false,
                    maxConcurrency: null, now.AddHours(1), now);
            }

            using var client = factory.CreateClient();
            using var tokenResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/token", UriKind.Relative),
                new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["operate"]));
            tokenResponse.EnsureSuccessStatusCode();
            var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/schedules", UriKind.Relative))
            {
                Content = JsonContent.Create(new CreateScheduleRequest(
                    repoId, [flowName], "0 6 * * *", null, "UTC", true, Name: scheduleName.ToUpperInvariant())),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains($"already has a schedule named '{scheduleName.ToUpperInvariant()}' (yaml schedule {yamlId})", body, StringComparison.Ordinal);

            await using var check = CatalogDatabase.Create(cs);
            Assert.Equal(yamlId, Assert.Single(await check.Schedules.AsNoTracking().Where(s => s.RepoId == repoId).ToListAsync()).Id);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }
}
