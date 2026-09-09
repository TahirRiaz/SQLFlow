using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The run-trigger surface (<c>POST /api/v1/runs</c>) end to end through the in-memory host. The authorization
/// tests (any authenticated token is accepted, since only user administration is scope-gated; a blank flow name is
/// a 400) need no database: authorization runs before the endpoint, and the flow-name guard runs before any catalog
/// query, so both resolve against the placeholder connection. The
/// 202 (a seeded active pipeline is accepted, with a runId and a Location header) and the 404 (an unknown pipeline)
/// are DB-backed and seed a repo + pipeline exactly like the read-API test, removing every seeded row in a finally.
/// The contract is references-only: the request carries a repo id and a flow name, never a secret.
/// </summary>
public sealed class RunTriggerApiTests
{
    [Fact]
    public async Task TriggerRun_WithAnyAuthenticatedToken_IsAuthorized()
    {
        // Triggering a run is part of the operational product every authenticated user gets: only user
        // administration is scope-gated. A token WITHOUT the operate scope therefore passes authorization and reaches
        // the endpoint's flow-name guard, which rejects a blank name with a 400 (proving it was not fenced at 403).
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostTriggerAsync(client, token, new RunTriggerRequest(Guid.NewGuid(), "  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TriggerRun_WithBlankFlowName_Returns400()
    {
        // An operate token passes authorization; the endpoint validates the flow name before any catalog query, so
        // a blank name is a 400 against the placeholder connection (no database needed).
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostTriggerAsync(client, token, new RunTriggerRequest(Guid.NewGuid(), "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerRun_WithOperateToken_ForSeededPipeline_Returns202WithRunIdAndLocation()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_trigger_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_trigger_orders_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
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
                    RemoteUrl = "https://example/" + repoName + ".git",
                    // A root path that does not host the flow file: the trigger still accepts (202); the background
                    // worker resolves the missing file, logs it, and skips. The endpoint's contract is "accepted for
                    // execution", not "executed".
                    RootPath = Path.Combine(Path.GetTempPath(), repoName),
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(SeedActivePipeline(pipelineId, repoId, flowName, now));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostTriggerAsync(client, token, new RunTriggerRequest(repoId, flowName));

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var accepted = await response.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(accepted);
            Assert.NotEqual(Guid.Empty, accepted.RunId);
            Assert.Equal("queued", accepted.Status);

            // The Location header points at the canonical run-detail endpoint for the minted run id.
            Assert.NotNull(response.Headers.Location);
            Assert.Equal($"/api/v1/runs/{accepted.RunId}", response.Headers.Location!.ToString());
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
    [Trait("Category", "Integration")]
    public async Task TriggerRun_WithOperateToken_ForUnknownPipeline_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        // A repo + flow that were never seeded: the pipeline existence check fails, so it is a 404 ProblemDetails.
        using var response = await PostTriggerAsync(
            client, token, new RunTriggerRequest(FlowIdentity.FromName("never_" + Guid.NewGuid().ToString("N")), "no_such_flow"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerRun_ExecutesOnTheWorker_AndRecordsUnderTheReturnedRunId()
    {
        // The contract that was silently broken before the assigned-id fix: the run id the trigger returns (and
        // puts in the Location header) must be the id the executed run is recorded under, so a client that polls
        // GET /api/v1/runs/{runId} actually finds its run. This drives the whole path end to end: trigger -> queue
        // -> background worker -> shared DocumentExecutor -> catalog write-back -> read API, against the real DB.
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_rt_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_rt_orders_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var tempTable = "cp_rt_" + suffix;
        // A per-test environment reference points the flow at the reachable catalog connection, so the worker's
        // engine resolves it through the normal ${env:...} path and the run genuinely executes. Removed in finally.
        var connEnvName = "SQLFLOW_CP_RT_" + suffix;
        var now = DateTime.UtcNow;

        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_cp_rt_" + suffix);
        var flowsDir = Path.Combine(dir, "flows");
        Directory.CreateDirectory(flowsDir);
        await File.WriteAllTextAsync(Path.Combine(flowsDir, "data.csv"), "id,name\n1,alpha\n2,beta\n");
        await File.WriteAllTextAsync(Path.Combine(flowsDir, "orders.flow.yaml"), $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:{{connEnvName}}}
              schema: dbo
              table: {{tempTable}}
            """);

        Environment.SetEnvironmentVariable(connEnvName, cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = "https://example/" + repoName + ".git",
                    RootPath = dir,
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "file",
                    RelativePath = "flows/orders.flow.yaml",
                    ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
                    Yaml = "name: " + flowName + "\n",
                    DefinitionJson = $$"""{"name":"{{flowName}}","flowKind":"file"}""",
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            Guid runId;
            using (var response = await PostTriggerAsync(client, token, new RunTriggerRequest(repoId, flowName)))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var accepted = await response.Content.ReadFromJsonAsync<RunTriggerAccepted>();
                Assert.NotNull(accepted);
                runId = accepted.RunId;
                Assert.NotEqual(Guid.Empty, runId);
            }

            // With the durable queue the run is recorded as 'queued' before the 202 returns, then the worker claims
            // it (running) and completes it. Poll the canonical run-detail endpoint until the run reaches a terminal
            // status, all under the id the trigger returned (bounded so a stuck worker fails rather than hangs).
            var reachedTerminal = false;
            var recordedRunId = Guid.Empty;
            string? status = null;
            string? error = null;
            var lastHttp = (HttpStatusCode)0;
            var lastBody = string.Empty;
            for (var attempt = 0; attempt < 120 && !reachedTerminal; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{runId}", UriKind.Relative));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var detail = await client.SendAsync(request);
                lastHttp = detail.StatusCode;
                lastBody = await detail.Content.ReadAsStringAsync();
                if (detail.StatusCode == HttpStatusCode.OK)
                {
                    using var doc = JsonDocument.Parse(lastBody);
                    recordedRunId = doc.RootElement.GetProperty("runId").GetGuid();
                    status = doc.RootElement.GetProperty("status").GetString();
                    error = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if (status is "succeeded" or "failed" or "cancelled")
                    {
                        reachedTerminal = true;
                        break;
                    }
                }
                else
                {
                    // The committed enqueue means GET is 200 almost immediately; a brief 404/429 is still tolerated.
                    Assert.True(
                        detail.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests,
                        $"unexpected status polling the run detail: {detail.StatusCode}");
                }

                await Task.Delay(250);
            }

            var dbStatus = "(unread)";
            try
            {
                await using var probe = new SqlConnection(cs);
                await probe.OpenAsync();
                await using var q = probe.CreateCommand();
                q.CommandText = "SELECT [Status] FROM [catalog].[Run] WHERE [RunId] = @id";
                var pid = q.CreateParameter();
                pid.ParameterName = "@id";
                pid.Value = runId;
                q.Parameters.Add(pid);
                dbStatus = (await q.ExecuteScalarAsync())?.ToString() ?? "(no row)";
            }
            catch (SqlException ex)
            {
                dbStatus = "(probe error: " + ex.Message + ")";
            }

            var workerLog = string.Join(Environment.NewLine, factory.Logs.Where(l => l.Contains("RunExecutionWorker", StringComparison.Ordinal)));
            Assert.True(reachedTerminal,
                $"the triggered run never reached a terminal status at GET /api/v1/runs/{runId} within the timeout. lastHttp={lastHttp}, dbStatus={dbStatus}, lastBody={lastBody}.{Environment.NewLine}Worker log:{Environment.NewLine}{workerLog}");
            // The crux: the recorded run is keyed by the exact id the trigger returned.
            Assert.Equal(runId, recordedRunId);
            // And the run genuinely executed (the CSV loaded into the temp table), proving the happy path end to end.
            Assert.Equal("succeeded", status);

            // The executed run also produced its consolidated trace: the engine's canonical events (published
            // live by the worker's sink, then re-projected from the run.json events array at completion) come back
            // from GET /runs/{id}/trace, including the per-file progress the file flow emitted.
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{runId}/trace?pageSize=200", UriKind.Relative)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var eventsResponse = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
                using var timeline = JsonDocument.Parse(await eventsResponse.Content.ReadAsStringAsync());
                var entries = timeline.RootElement.GetProperty("items").EnumerateArray().ToList();
                Assert.NotEmpty(entries);
                var eventEntries = entries.Where(e => e.GetProperty("kind").GetString() == "event").ToList();
                Assert.NotEmpty(eventEntries);
                // The file flow's canonical progress is in the feed: it read data.csv (the source.open event).
                Assert.Contains(eventEntries, e =>
                    e.GetProperty("message").GetString()!.Contains("data.csv", StringComparison.OrdinalIgnoreCase));
                Assert.All(eventEntries, e =>
                    Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("level").GetString())));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connEnvName, null);
            try
            {
                await using var clean = new SqlConnection(cs);
                await clean.OpenAsync();
                await using var drop = clean.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tempTable}]";
                await drop.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
                // Best-effort cleanup: a never-created table (a failed run) leaves nothing to drop.
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
                await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
                await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
                await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A transient lock on a run-log file must not fail the test; the temp dir is disposable.
            }
        }
    }

    [Fact]
    public async Task CancelRun_WithAnyAuthenticatedToken_IsAuthorized()
    {
        // Cancelling a run is part of the operational product every authenticated user gets, like triggering: only
        // user administration is scope-gated. A token WITHOUT the operate scope therefore clears authorization
        // (the response is neither 401 nor 403); the run lookup itself happens past that boundary.
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostCancelAsync(client, token, Guid.NewGuid());

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CancelRun_ForUnknownRun_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostCancelAsync(client, token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CancelRun_ForRunningRun_Returns202Cancelling_AndStampsTheRequest()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_cancel_" + suffix);
        var flowName = "cp_cancel_orders_" + suffix;
        var runId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            // Seed a run that is already running, claimed by a node that is NOT this host, so the control-plane
            // worker never touches it: the cancel is therefore a pure request (202 "cancelling"), left for the
            // (absent) owning node to honor. This exercises the endpoint's running-run branch deterministically.
            await using (var db = CatalogDatabase.Create(cs))
            {
                // The fabricated running run must be claimed by a LIVE node: a real running run always has a
                // heartbeating claimant, and the host's orphan reaper (rightly) reclaims one that does not.
                await NodeStore.HeartbeatAsync(db, "some-remote-node-" + suffix, "1.0.0", DateTime.UtcNow);
                db.Runs.Add(new CatalogRun
                {
                    RunId = runId,
                    PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
                    RepoId = repoId,
                    FlowName = flowName,
                    FlowKind = "ing",
                    Status = RunStatuses.Running,
                    ClaimedByNode = "some-remote-node-" + suffix,
                    EnqueuedUtc = now,
                    StartUtc = now,
                    WrittenUtc = now,
                    Success = false,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostCancelAsync(client, token, runId);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(accepted);
            Assert.Equal(runId, accepted.RunId);
            Assert.Equal("cancelling", accepted.Status);
            Assert.Equal($"/api/v1/runs/{runId}", response.Headers.Location!.ToString());

            // The request is durable on the row for the owning node to observe.
            await using var probe = CatalogDatabase.Create(cs);
            var stored = await probe.Runs.AsNoTracking().FirstAsync(r => r.RunId == runId);
            Assert.Equal(RunStatuses.Running, stored.Status);
            Assert.NotNull(stored.CancelRequestedUtc);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await NodeStore.DeleteAsync(db, "some-remote-node-" + suffix);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerRun_WithPool_QueuesItForThatPool_AndTheUntargetedWorkerLeavesItQueued()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_pool_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_pool_orders_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var pool = "pool-" + suffix;
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
                    RootPath = Path.Combine(Path.GetTempPath(), repoName),
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(SeedActivePipeline(pipelineId, repoId, flowName, now));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostTriggerAsync(client, token, new RunTriggerRequest(repoId, flowName, pool));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(accepted);
            var runId = accepted.RunId;

            // The control-plane worker serves no pools, so it must never claim this pool-routed run. Give it several
            // poll cycles to (wrongly) claim it, then confirm it is still queued and carries its target pool.
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(250);
            }

            using var get = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{runId}", UriKind.Relative));
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var detail = await client.SendAsync(get);
            detail.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(pool, doc.RootElement.GetProperty("targetPool").GetString());
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static Task<HttpResponseMessage> PostCancelAsync(HttpClient client, string token, Guid runId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/runs/{runId}/cancel", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static CatalogPipeline SeedActivePipeline(Guid id, Guid repoId, string name, DateTime now)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            Kind = "ing",
            RelativePath = "flows/" + name + ".flow.yaml",
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\nflowType: ing\n",
            DefinitionJson = $$"""{"name":"{{name}}","flowType":"ing"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static Task<HttpResponseMessage> PostTriggerAsync(HttpClient client, string token, RunTriggerRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/runs", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        return token.AccessToken;
    }
}
