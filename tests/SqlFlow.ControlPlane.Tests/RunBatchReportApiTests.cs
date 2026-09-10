using System.Data.SqlTypes;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The batch dimension of the runs read API end to end against the real shadow catalog: every run reports the
/// batch label and lineage wave of its pipeline row (joined at query time, like the classic batch report), a flow
/// with no YAML batch or a run whose pipeline left the catalog falls back to
/// <see cref="CatalogPipeline.DefaultBatch"/> with wave -1, and the <c>batch</c> query filter narrows the list.
/// <c>latest=true</c> reduces the list to each pipeline's newest run (the batch status board), with the status
/// filter applying to that latest run ("currently failed", not "ever failed"). Seeded rows are removed in a
/// finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunBatchReportApiTests
{
    [SkippableFact]
    public async Task RunsApi_JoinsBatchAndWaveFromPipeline_WithDefaultFallbackAndFilter()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_batch_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var batchedFlow = "cp_line_" + suffix;
        var unbatchedFlow = "cp_calls_" + suffix;
        var batchName = "apc_dalane_" + suffix;
        var batchedPipelineId = CatalogIdentity.Pipeline(repoId, batchedFlow);
        var unbatchedPipelineId = CatalogIdentity.Pipeline(repoId, unbatchedFlow);
        var batchedOldRunId = Guid.CreateVersion7();
        var batchedRunId = Guid.CreateVersion7();
        var unbatchedRunId = Guid.CreateVersion7();
        var orphanRunId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;

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
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(SeedPipeline(batchedPipelineId, repoId, batchedFlow, batchName, wave: 2, now));
                db.Pipelines.Add(SeedPipeline(unbatchedPipelineId, repoId, unbatchedFlow, batch: null, wave: -1, now));
                // The batched pipeline failed five minutes ago and succeeded since: its CURRENT state is green.
                db.Runs.Add(SeedRun(batchedOldRunId, batchedPipelineId, repoId, batchedFlow, now.AddMinutes(-5), succeeded: false));
                db.Runs.Add(SeedRun(batchedRunId, batchedPipelineId, repoId, batchedFlow, now, succeeded: true));
                // The unbatched pipeline's one and only run failed: it is what the status board must surface.
                db.Runs.Add(SeedRun(unbatchedRunId, unbatchedPipelineId, repoId, unbatchedFlow, now.AddSeconds(1), succeeded: false));
                // A run whose pipeline row is gone from the catalog (the flow left the estate): the left join must
                // still list it, under the default batch.
                db.Runs.Add(SeedRun(orphanRunId, Guid.NewGuid(), repoId, "cp_gone_" + suffix, now.AddSeconds(2), succeeded: true));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var all = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&pageSize=200");
            Assert.Equal(4, all.Total);

            var batched = Assert.Single(all.Items, r => r.RunId == batchedRunId);
            Assert.Equal(batchName, batched.Batch);
            Assert.Equal(2, batched.Wave);

            var unbatched = Assert.Single(all.Items, r => r.RunId == unbatchedRunId);
            Assert.Equal(CatalogPipeline.DefaultBatch, unbatched.Batch);
            Assert.Equal(-1, unbatched.Wave);

            var orphan = Assert.Single(all.Items, r => r.RunId == orphanRunId);
            Assert.Equal(CatalogPipeline.DefaultBatch, orphan.Batch);
            Assert.Equal(-1, orphan.Wave);

            // The batch filter narrows the list to the labelled runs (exact label match: the board's batch
            // dropdown supplies a real label, so "trans" must not also drag in "trans_item").
            var filtered = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&batch={batchName}&pageSize=200");
            Assert.Equal(2, filtered.Total);
            Assert.All(filtered.Items, r => Assert.Equal(batchName, r.Batch));

            // latest=true is the batch status board: one row per pipeline, its newest run. The batched pipeline
            // reports its recovery (the succeeded run), not the failure it already got past.
            var latest = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&latest=true&pageSize=200");
            Assert.Equal(3, latest.Total);
            Assert.DoesNotContain(latest.Items, r => r.RunId == batchedOldRunId);
            Assert.Contains(latest.Items, r => r.RunId == batchedRunId);

            // The status board reads in report order: batch label ascending ("apc_..." before "default"), so the
            // labelled pipeline's latest run leads and the default-batch rows follow as one group.
            Assert.Equal(batchedRunId, latest.Items[0].RunId);
            Assert.Equal(
                latest.Items.Select(r => r.Batch).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList(),
                latest.Items.Select(r => r.Batch).ToList(),
                StringComparer.OrdinalIgnoreCase);

            // status filters compose AFTER the latest cut: "failed" means currently failed, so only the
            // unbatched pipeline (whose newest run failed) surfaces as needing a fix.
            var currentlyFailed = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&latest=true&status=failed&pageSize=200");
            Assert.Equal(1, currentlyFailed.Total);
            Assert.Equal(unbatchedRunId, Assert.Single(currentlyFailed.Items).RunId);

            // Without the latest cut the same status filter is history: every failed run, ever.
            var everFailed = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&status=failed&pageSize=200");
            Assert.Equal(2, everFailed.Total);

            // Filtering on the fallback label finds the unbatched and orphaned runs.
            var defaulted = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&batch={CatalogPipeline.DefaultBatch}&pageSize=200");
            Assert.Equal(2, defaulted.Total);
            Assert.All(defaulted.Items, r => Assert.Equal(CatalogPipeline.DefaultBatch, r.Batch));

            // The run detail carries the same joined batch and wave, for the labelled and the orphaned run alike.
            var batchedDetail = await GetJsonAsync<RunDetailDto>(client, token, $"/api/v1/runs/{batchedRunId}");
            Assert.Equal(batchName, batchedDetail.Batch);
            Assert.Equal(2, batchedDetail.Wave);

            var orphanDetail = await GetJsonAsync<RunDetailDto>(client, token, $"/api/v1/runs/{orphanRunId}");
            Assert.Equal(CatalogPipeline.DefaultBatch, orphanDetail.Batch);
            Assert.Equal(-1, orphanDetail.Wave);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task RunsApi_LatestBreaksWrittenUtcTies_ByRunIdDescending()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_tie_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_tie_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var now = DateTime.UtcNow;

        // Two runs of one pipeline written at the SAME instant: the board must surface the one with the
        // greater RunId, compared the way SQL Server orders uniqueidentifier (SqlGuid, not Guid.CompareTo),
        // because that is the comparison the query's ORDER BY RunId DESC evaluates in.
        var tieA = Guid.CreateVersion7();
        var tieB = Guid.CreateVersion7();
        var expectedWinner = new SqlGuid(tieA).CompareTo(new SqlGuid(tieB)) > 0 ? tieA : tieB;

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
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(SeedPipeline(pipelineId, repoId, flowName, batch: null, wave: 0, now));
                db.Runs.Add(SeedRun(tieA, pipelineId, repoId, flowName, now, succeeded: true));
                db.Runs.Add(SeedRun(tieB, pipelineId, repoId, flowName, now, succeeded: false));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var latest = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?repoId={repoId}&latest=true&pageSize=200");
            Assert.Equal(1, latest.Total);
            Assert.Equal(expectedWinner, Assert.Single(latest.Items).RunId);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline SeedPipeline(Guid id, Guid repoId, string name, string? batch, int wave, DateTime now)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            Kind = "file",
            Batch = batch,
            RelativePath = $"flows/{name}.flow.yaml",
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\nflowType: file\n",
            DefinitionJson = $$"""{"name":"{{name}}","flowType":"file"}""",
            Active = true,
            Wave = wave,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogRun SeedRun(
        Guid runId, Guid pipelineId, Guid repoId, string flowName, DateTime writtenUtc, bool succeeded)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "file",
            Success = succeeded,
            Status = succeeded ? RunStatuses.Succeeded : RunStatuses.Failed,
            Error = succeeded ? null : "seeded failure",
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
