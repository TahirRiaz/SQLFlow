using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The confirmation gate on the ad-hoc query surface, end to end. The point of the two-step shape is that the
/// approval is ENFORCED, not merely requested: the run endpoint takes a token and never a statement, so the
/// only executable SQL is SQL that was first prepared and handed back to be shown. These tests pin that no
/// path skips it, that a token is single-use, and that a lapsed approval is not a standing permission.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QueryApprovalApiTests
{
    [SkippableFact]
    public async Task Prepare_ValidatesAndMintsAToken_AndRunRedeemsItExactlyOnce()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_QUERY_" + suffix + "}";
        var repoId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:DataOps:Enabled", "true");
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "q_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = "q_flow_" + suffix,
                    Kind = "ing",
                    RelativePath = "q/flow.yaml",
                    Active = true,
                    SourceServer = serverRef,
                    TargetServer = serverRef,
                    DefinitionJson = "{}",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            // 1. A statement that is not read-only never gets a token at all.
            using var refused = await PostAsync(client, token, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest("DELETE FROM edw.F", serverRef));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            // 2. A real question prepares, and comes back with the EXACT sql to show plus a token. Nothing has
            //    run: no compute task exists yet.
            using var preparedResponse = await PostAsync(client, token, "/api/v1/dataops/queries/prepare",
                new PrepareQueryRequest("SELECT TOP 5 * FROM edw.F", serverRef, Database: "dw"));
            preparedResponse.EnsureSuccessStatusCode();
            var prepared = await preparedResponse.Content.ReadFromJsonAsync<PreparedQueryDto>();
            Assert.NotNull(prepared);
            Assert.Equal("SELECT TOP 5 * FROM edw.F", prepared.Sql);
            Assert.NotEqual(Guid.Empty, prepared.PlanId);
            Assert.True(prepared.ExpiresUtc > now);

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(0, await db.ComputeTasks.CountAsync(t => t.SourceRef == serverRef));
            }

            // 3. Redeeming the token queues the approved query, and the queued payload carries the statement
            //    the caller was shown, not something else.
            using var runResponse = await PostAsync(
                client, token, $"/api/v1/dataops/queries/{prepared.PlanId}/run", new { });
            Assert.Equal(HttpStatusCode.Accepted, runResponse.StatusCode);
            var accepted = await runResponse.Content.ReadFromJsonAsync<ComputeTaskAccepted>();
            Assert.NotNull(accepted);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var task = await db.ComputeTasks.AsNoTracking().FirstAsync(t => t.TaskId == accepted.TaskId);
                Assert.Equal("runQuery", task.Operation);
                Assert.Contains("SELECT TOP 5 * FROM edw.F", task.ArgumentsJson, StringComparison.Ordinal);

                var plan = await db.QueryPlans.AsNoTracking().FirstAsync(p => p.PlanId == prepared.PlanId);
                Assert.NotNull(plan.ConsumedUtc);
                Assert.Equal(accepted.TaskId, plan.TaskId);
            }

            // 4. The token is spent. Replaying it does not run the query a second time.
            using var replay = await PostAsync(
                client, token, $"/api/v1/dataops/queries/{prepared.PlanId}/run", new { });
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

            // 5. An unknown token is refused rather than treated as approval.
            using var unknown = await PostAsync(
                client, token, $"/api/v1/dataops/queries/{Guid.NewGuid()}/run", new { });
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            // 6. An expired approval is not a standing permission.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.QueryPlans.Where(p => p.PlanId == prepared.PlanId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.ConsumedUtc, (DateTime?)null)
                        .SetProperty(p => p.ExpiresUtc, now.AddMinutes(-1)));
            }

            using var expired = await PostAsync(
                client, token, $"/api/v1/dataops/queries/{prepared.PlanId}/run", new { });
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.QueryPlans.Where(p => p.SourceRef == serverRef).ExecuteDeleteAsync();
            await db.ComputeTasks.Where(t => t.SourceRef == serverRef).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task WithTheSwitchOff_TheWholeQuerySurfaceIsRefused()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // The default: DataOps is off unless a deployment turns it on.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client);

        using var prepare = await PostAsync(client, token, "/api/v1/dataops/queries/prepare",
            new PrepareQueryRequest("SELECT 1", "${env:SQLFLOW_ANY}"));
        Assert.Equal(HttpStatusCode.Forbidden, prepare.StatusCode);

        using var run = await PostAsync(
            client, token, $"/api/v1/dataops/queries/{Guid.NewGuid()}/run", new { });
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    private static async Task<string> IssueTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativeUri, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
