using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The time-window dimension of the runs read API end to end against the real shadow catalog: the schedules
/// timeline reads runs by day, so the list takes optional inclusive <c>from</c>/<c>to</c> bounds on
/// <see cref="RunSummaryDto.WrittenUtc"/>. A closed window keeps only the runs inside it, an open-ended window
/// (one bound omitted) narrows in a single direction, and the bounds compose with the existing filters. Seeded
/// rows are removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunTimeWindowApiTests
{
    [SkippableFact]
    public async Task RunsApi_FromTo_WindowsHistoryByWrittenUtc()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_window_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_window_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);

        // Anchor everything to a fixed instant so the window bounds are deterministic (no wall-clock in the test).
        var anchor = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var old = anchor.AddDays(-20);   // outside a 14-day window
        var recent = anchor.AddDays(-3); // inside a 14-day window
        var newest = anchor.AddHours(-1);
        var oldRunId = Guid.CreateVersion7();
        var recentRunId = Guid.CreateVersion7();
        var newestRunId = Guid.CreateVersion7();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = null,
                    RootPath = "/tmp/" + repoName,
                    FirstSeenUtc = old,
                    LastSyncUtc = anchor,
                });
                db.Pipelines.Add(SeedPipeline(pipelineId, repoId, flowName, anchor));
                db.Runs.Add(SeedRun(oldRunId, pipelineId, repoId, flowName, old));
                db.Runs.Add(SeedRun(recentRunId, pipelineId, repoId, flowName, recent));
                db.Runs.Add(SeedRun(newestRunId, pipelineId, repoId, flowName, newest));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // No window: the full history for the pipeline (all three runs).
            var all = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&pageSize=200");
            Assert.Equal(3, all.Total);

            // A 14-day window ending at the anchor keeps the two recent runs and drops the 20-day-old one.
            var from14 = anchor.AddDays(-14);
            var windowed = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token,
                $"/api/v1/runs?pipelineId={pipelineId}&from={Iso(from14)}&to={Iso(anchor)}&pageSize=200");
            Assert.Equal(2, windowed.Total);
            Assert.Contains(windowed.Items, r => r.RunId == recentRunId);
            Assert.Contains(windowed.Items, r => r.RunId == newestRunId);
            Assert.DoesNotContain(windowed.Items, r => r.RunId == oldRunId);

            // Open-ended lower bound only: everything at or after the 14-day mark (same two recent runs).
            var fromOnly = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&from={Iso(from14)}&pageSize=200");
            Assert.Equal(2, fromOnly.Total);

            // Open-ended upper bound only: everything at or before the 2-day mark (the 20-day-old run and the
            // 3-day-old run, not the run from an hour ago). Proves `to` filters independently of `from`.
            var toOnly = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&to={Iso(anchor.AddDays(-2))}&pageSize=200");
            Assert.Equal(2, toOnly.Total);
            Assert.Contains(toOnly.Items, r => r.RunId == oldRunId);
            Assert.Contains(toOnly.Items, r => r.RunId == recentRunId);
            Assert.DoesNotContain(toOnly.Items, r => r.RunId == newestRunId);

            // A window that predates every run is empty (a real "nothing ran then", not an error).
            var empty = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token,
                $"/api/v1/runs?pipelineId={pipelineId}&from={Iso(anchor.AddDays(-60))}&to={Iso(anchor.AddDays(-40))}&pageSize=200");
            Assert.Equal(0, empty.Total);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static string Iso(DateTime utc)
        => Uri.EscapeDataString(utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    private static CatalogPipeline SeedPipeline(Guid id, Guid repoId, string name, DateTime now)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            Kind = "file",
            Batch = null,
            RelativePath = $"flows/{name}.flow.yaml",
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\nflowType: file\n",
            DefinitionJson = $$"""{"name":"{{name}}","flowType":"file"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogRun SeedRun(Guid runId, Guid pipelineId, Guid repoId, string flowName, DateTime writtenUtc)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "file",
            Success = true,
            Status = RunStatuses.Succeeded,
            Error = null,
            SchemaVersion = 1,
            WrittenUtc = writtenUtc,
            DurationSeconds = 0.5,
            RowsLoaded = 3,
        };

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
