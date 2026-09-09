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
/// The insights read surface: authentication, and the aggregation math over seeded run history. One seeded
/// estate exercises all three endpoints: a slow flow with a failure (flows + attention), per-step events and
/// statements (steps), and the grouped EF queries whose translation only a real database proves. Removes its
/// own rows; the assembly runs serially (see AssemblyInfo).
/// </summary>
public sealed class InsightsApiTests
{
    [Fact]
    public async Task Insights_WithoutAToken_Returns401()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/insights/flows", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Insights_AggregateFlowsAttentionAndSteps()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("ins_" + suffix);
        var flowName = "ins_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "ins_repo_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    Batch = "ins_batch_" + suffix,
                    RelativePath = "flows/" + flowName + ".flow.yaml",
                    ContentHash = new string('0', 64),
                    Yaml = "name: " + flowName + "\n",
                    DefinitionJson = "{}",
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });

                // Two succeeded runs in the current window (durations 100s and 200s), one failed run as the
                // NEWEST, and one succeeded run in the previous window (50s) so the trend computes: current
                // succeeded avg 150s vs previous 50s = +200%.
                var previousRun = Guid.NewGuid();
                var succeededOld = Guid.NewGuid();
                var succeededNew = Guid.NewGuid();
                var failedNewest = Guid.NewGuid();
                db.Runs.AddRange(
                    new CatalogRun
                    {
                        RunId = previousRun, PipelineId = pipelineId, RepoId = repoId, FlowName = flowName,
                        FlowKind = "ing", Success = true, Status = RunStatuses.Succeeded,
                        WrittenUtc = now.AddDays(-10), DurationSeconds = 50, RowsLoaded = 500,
                    },
                    new CatalogRun
                    {
                        RunId = succeededOld, PipelineId = pipelineId, RepoId = repoId, FlowName = flowName,
                        FlowKind = "ing", Success = true, Status = RunStatuses.Succeeded,
                        WrittenUtc = now.AddDays(-3), DurationSeconds = 100, RowsLoaded = 1000,
                    },
                    new CatalogRun
                    {
                        RunId = succeededNew, PipelineId = pipelineId, RepoId = repoId, FlowName = flowName,
                        FlowKind = "ing", Success = true, Status = RunStatuses.Succeeded,
                        WrittenUtc = now.AddDays(-2), DurationSeconds = 200, RowsLoaded = 3000,
                    },
                    new CatalogRun
                    {
                        RunId = failedNewest, PipelineId = pipelineId, RepoId = repoId, FlowName = flowName,
                        FlowKind = "ing", Success = false, Status = RunStatuses.Failed,
                        WrittenUtc = now.AddDays(-1), DurationSeconds = 5, Error = "boom " + suffix,
                    });

                // Step timings on the newest succeeded run, plus its statements for the sample SQL.
                db.RunEvents.AddRange(
                    new CatalogRunEvent
                    {
                        RunId = succeededNew, RepoId = repoId, Ordinal = 1, TimestampUtc = now.AddDays(-2),
                        Level = "info", Step = "staging.load", Message = "loaded", Rows = 3000, ElapsedMs = 180_000,
                    },
                    new CatalogRunEvent
                    {
                        RunId = succeededNew, RepoId = repoId, Ordinal = 2, TimestampUtc = now.AddDays(-2),
                        Level = "info", Step = "target.merge", Message = "merged", Rows = 3000, ElapsedMs = 15_000,
                    });
                db.RunStatements.Add(new CatalogRunStatement
                {
                    RunId = succeededNew, RepoId = repoId, Ordinal = 1, TimestampUtc = now.AddDays(-2),
                    Step = "staging.load", Sql = "SELECT 1 /* " + suffix + " */",
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            var flows = await GetJsonAsync<FlowInsightsDto>(
                client, token, $"/api/v1/insights/flows?days=7&repoId={repoId}");
            var flow = Assert.Single(flows.Flows, f => f.PipelineId == pipelineId);
            Assert.Equal(3, flow.Runs);
            Assert.Equal(1, flow.Failures);
            Assert.NotNull(flow.AvgDurationSeconds);
            Assert.Equal(150, flow.AvgDurationSeconds!.Value, 1); // succeeded runs only: (100 + 200) / 2
            Assert.Equal(305, flow.TotalDurationSeconds, 1); // every terminal run: 100 + 200 + 5
            Assert.Equal(4000, flow.RowsLoaded);
            Assert.Equal(RunStatuses.Failed, flow.LastStatus);
            Assert.Contains("boom", flow.LastError, StringComparison.Ordinal);
            Assert.NotNull(flow.PrevAvgDurationSeconds);
            Assert.Equal(50, flow.PrevAvgDurationSeconds!.Value, 1);
            Assert.Equal(500, flow.PrevRowsLoaded);
            Assert.NotNull(flow.DurationTrendPercent);
            Assert.Equal(200, flow.DurationTrendPercent!.Value, 1);

            // The flow trips two rules (last run failed, getting slower) but occupies ONE slot: the most
            // urgent finding wins and the other folds into the "Also:" note.
            var attention = await GetJsonAsync<AttentionDto>(
                client, token, $"/api/v1/insights/attention?days=7&repoId={repoId}");
            var flowItem = Assert.Single(attention.Items, i => i.PipelineId == pipelineId);
            Assert.Equal("last-run-failed", flowItem.Category);
            Assert.Contains("Also: getting slower", flowItem.Detail, StringComparison.Ordinal);
            Assert.True(attention.TotalItems >= 1);
            Assert.True(attention.WarningCount >= 1);

            // A completed warehouse probe task feeds the recommendations with its suggested DDL.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.ComputeTasks.Add(new CatalogComputeTask
                {
                    TaskId = Guid.NewGuid(),
                    Operation = "missingIndexes",
                    SourceRef = "${env:INS_TEST_" + suffix + "}",
                    ArgumentsJson = "{\"operation\":\"missingIndexes\",\"sourceRef\":\"${env:INS_TEST_" + suffix + "}\"}",
                    Status = RunStatuses.Succeeded,
                    EnqueuedUtc = now,
                    StartUtc = now,
                    EndUtc = now,
                    ResultJson =
                        "{\"database\":\"dw\",\"advisories\":[{\"database\":\"dw\",\"schema\":\"arc\"," +
                        "\"table\":\"Ins_" + suffix + "\",\"equalityColumns\":\"[OrderId]\"," +
                        "\"userSeeks\":4200,\"userScans\":0,\"avgTotalUserCost\":12.5," +
                        "\"avgUserImpactPercent\":93.0,\"improvementMeasure\":4882500.0," +
                        "\"suggestedIndexSql\":\"CREATE NONCLUSTERED INDEX [IX_Ins] ON [arc].[Ins] ([OrderId]);\"}]}",
                });
                await db.SaveChangesAsync();
            }

            // Compact by default: the advisory names the SQL's existence without carrying it.
            var compact = await GetJsonAsync<RecommendationsDto>(
                client, token, $"/api/v1/insights/recommendations?days=7&repoId={repoId}");
            Assert.Contains(compact.Items, i => i.Category == "last-run-failed" && i.PipelineId == pipelineId);
            var compactIndex = Assert.Single(
                compact.Items, i => i.Category == "missing-index" && i.Title.Contains(suffix, StringComparison.Ordinal));
            Assert.True(compactIndex.HasSuggestedSql);
            Assert.Null(compactIndex.SuggestedSql);
            Assert.True(compact.TotalItems >= 2);

            // includeSql=true is the full form the GUI reads.
            var full = await GetJsonAsync<RecommendationsDto>(
                client, token, $"/api/v1/insights/recommendations?days=7&repoId={repoId}&includeSql=true");
            var missingIndex = Assert.Single(
                full.Items, i => i.Category == "missing-index" && i.Title.Contains(suffix, StringComparison.Ordinal));
            Assert.Equal("warehouseDmv", missingIndex.Source);
            Assert.Equal("warning", missingIndex.Severity); // improvementMeasure over the 1M threshold
            Assert.Contains("CREATE NONCLUSTERED INDEX", missingIndex.SuggestedSql, StringComparison.Ordinal);
            Assert.Contains(full.WarehouseProbes, p => p.Operation == "missingIndexes");

            // The compact default carries timings only; SQL bodies travel on request.
            var compactSteps = await GetJsonAsync<StepInsightsDto>(
                client, token, $"/api/v1/insights/pipelines/{pipelineId}/steps?days=7");
            Assert.All(compactSteps.Steps, s => Assert.Null(s.SampleSql));

            var steps = await GetJsonAsync<StepInsightsDto>(
                client, token, $"/api/v1/insights/pipelines/{pipelineId}/steps?days=7&includeSql=true");
            Assert.Equal(flowName, steps.FlowName);
            Assert.Equal(2, steps.Steps.Count);
            Assert.Equal("staging.load", steps.Steps[0].Step); // ordered by total elapsed
            Assert.Equal(180_000, steps.Steps[0].TotalElapsedMs, 1);
            Assert.NotNull(steps.Steps[0].SampleSql);
            Assert.Contains(suffix, steps.Steps[0].SampleSql, StringComparison.Ordinal);
            Assert.Null(steps.Steps[1].SampleSql); // target.merge recorded no statement

            using var missing = new HttpRequestMessage(
                HttpMethod.Get, new Uri($"/api/v1/insights/pipelines/{Guid.NewGuid()}/steps", UriKind.Relative));
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var missingResponse = await client.SendAsync(missing);
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var testRef = "${env:INS_TEST_" + suffix + "}";
            await db.ComputeTasks.Where(t => t.SourceRef == testRef).ExecuteDeleteAsync();
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
