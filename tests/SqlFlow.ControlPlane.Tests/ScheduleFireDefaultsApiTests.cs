using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A schedule created through the API resolves an omitted <c>maxConcurrency</c> with the default the host's product
/// registered (<see cref="ScheduleFireDefaults"/>), as a schedule read from git does, and keeps a bound it was given.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduleFireDefaultsApiTests
{
    [SkippableFact]
    public async Task AnApiSchedule_TakesTheHostsDefault_WhenItGivesNoBound()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("firewidth_" + suffix);
        var flowName = "orders_" + suffix;

        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithServices(services => services.AddSingleton(ScheduleFireDefaults.Unbounded))
            .WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = "firewidth_" + suffix,
                    RemoteUrl = "https://git.invalid/firewidth.git",
                    RootPath = Path.Combine(Path.GetTempPath(), "firewidth_" + suffix),
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
            }

            using var client = factory.CreateClient();
            using var tokenResponse = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/token", UriKind.Relative),
                new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["operate"]));
            tokenResponse.EnsureSuccessStatusCode();
            var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;

            var untuned = await CreateAsync(client, token, new CreateScheduleRequest(
                repoId, [flowName], "0 6 * * *", null, "UTC", true, Name: "untuned_" + suffix));
            var bounded = await CreateAsync(client, token, new CreateScheduleRequest(
                repoId, [flowName], "0 7 * * *", null, "UTC", true, Name: "bounded_" + suffix, MaxConcurrency: 3));

            await using var check = CatalogDatabase.Create(cs);
            Assert.Null((await check.Schedules.AsNoTracking().FirstAsync(s => s.Id == untuned)).MaxConcurrency);
            Assert.Equal(3, (await check.Schedules.AsNoTracking().FirstAsync(s => s.Id == bounded)).MaxConcurrency);
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

    private static async Task<Guid> CreateAsync(HttpClient client, string token, CreateScheduleRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/schedules", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScheduleCreated>())!.Id;
    }
}
