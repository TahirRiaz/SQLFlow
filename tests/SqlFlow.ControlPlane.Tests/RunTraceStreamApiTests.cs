using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The live run trace stream (<c>GET /runs/{id}/trace/stream</c>) end to end against the real catalog: rows
/// inserted while the SSE connection is open arrive as <c>entry</c> frames (proving the server-side tail pushes
/// deltas mid-run), the terminal status produces exactly one <c>end</c> frame and closes the stream, the id
/// cursors skip entries the client already holds, and an unknown run is a 404 before any streaming starts.
/// Gated on a reachable catalog database, like the other DB-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunTraceStreamApiTests
{
    [SkippableFact]
    public async Task Stream_PushesEntriesAsTheyLand_ResumesFromCursors_AndEndsOnTerminalStatus()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_stream_" + suffix);
        var flowName = "cp_stream_flow_" + suffix;
        var runId = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 9, 8, 0, 0, DateTimeKind.Utc);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;

        try
        {
            long preSeededEventId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                // The fabricated running run must be claimed by a LIVE node: a real running run always has a
                // heartbeating claimant, and the host's orphan reaper (rightly) reclaims one that does not.
                await NodeStore.HeartbeatAsync(db, "cp-stream-node-" + suffix, "1.0.0", DateTime.UtcNow, ct: ct);
                db.Runs.Add(new CatalogRun
                {
                    RunId = runId,
                    PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
                    RepoId = repoId,
                    FlowName = flowName,
                    FlowKind = "ing",
                    Status = RunStatuses.Running,
                    ClaimedByNode = "cp-stream-node-" + suffix,
                    Success = false,
                    WrittenUtc = t0,
                });
                // Already-held entry: the client passes its id as the cursor, so the stream must NOT replay it.
                var preSeeded = new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, TimestampUtc = t0, Level = "info",
                    Step = "flow", Message = "already on the client",
                };
                db.RunEvents.Add(preSeeded);
                await db.SaveChangesAsync(ct);
                preSeededEventId = preSeeded.Id;
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            using var missing = await SendStreamAsync(client, token, $"/api/v1/runs/{Guid.NewGuid()}/trace/stream", ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using var response = await SendStreamAsync(
                client, token, $"/api/v1/runs/{runId}/trace/stream?afterEventId={preSeededEventId}", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var frames = new SseFrameReader(body);

            // Rows landing while the connection is open: the tail must push them without a new request.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 2, TimestampUtc = t0.AddSeconds(1), Level = "info",
                    Step = "source.open", Message = "read 'a.csv' (31 row(s))", Rows = 31,
                });
                db.RunStatements.Add(new CatalogRunStatement
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, TimestampUtc = t0.AddSeconds(2),
                    Step = "staging.create", Sql = "CREATE TABLE #s;",
                });
                await db.SaveChangesAsync(ct);
            }

            var first = await frames.ReadAsync("entry", ct);
            var firstEntry = JsonSerializer.Deserialize<RunTraceEntryDto>(first, JsonWeb.Options);
            Assert.NotNull(firstEntry);
            Assert.Equal("event", firstEntry.Kind);
            Assert.Equal("read 'a.csv' (31 row(s))", firstEntry.Message);

            var second = await frames.ReadAsync("entry", ct);
            var secondEntry = JsonSerializer.Deserialize<RunTraceEntryDto>(second, JsonWeb.Options);
            Assert.NotNull(secondEntry);
            Assert.Equal("statement", secondEntry.Kind);
            Assert.Equal("CREATE TABLE #s;", secondEntry.Sql);

            // The terminal status ends the stream with exactly one end frame carrying that status.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == runId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Succeeded), ct);
            }

            var end = await frames.ReadAsync("end", ct);
            var endPayload = JsonSerializer.Deserialize<RunTraceStreamEndDto>(end, JsonWeb.Options);
            Assert.NotNull(endPayload);
            Assert.Equal(RunStatuses.Succeeded, endPayload.Status);
            Assert.True(await frames.AtEndAsync(ct), "the stream must close after the end frame");
        }
        finally
        {
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            await using var db = CatalogDatabase.Create(cs);
            await db.RunEvents.Where(e => e.RunId == runId).ExecuteDeleteAsync();
            await db.RunStatements.Where(s => s.RunId == runId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RunId == runId).ExecuteDeleteAsync();
            await NodeStore.DeleteAsync(db, "cp-stream-node-" + suffix);
        }
    }

    private static class JsonWeb
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
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

    private static Task<HttpResponseMessage> SendStreamAsync(
        HttpClient client, string token, string relativeUri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
