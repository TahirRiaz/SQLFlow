using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The live run group stream (<c>GET /runs/groups/{id}/stream</c>) end to end against the real catalog: the
/// connect snapshot delivers every member's summary in execution order, a trace event landing for a member
/// republishes that member with the new "last action" (the batch overview's live column), status flips
/// republish too, and once every member is terminal the stream sends one <c>end</c> frame with the final
/// rollup and closes. An unknown group is a 404 before any streaming starts. Gated on a reachable catalog
/// database, like the other DB-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunGroupStreamApiTests
{
    [SkippableFact]
    public async Task GroupStream_SnapshotsMembers_PushesLastActionAndStatusChanges_AndEndsWithTheRollup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_gstream_" + suffix);
        var flowA = "cp_gstream_a_" + suffix;
        var flowB = "cp_gstream_b_" + suffix;
        var groupId = Guid.NewGuid();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 9, 14, 0, 0, DateTimeKind.Utc);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                // The fabricated running member must be claimed by a LIVE node: a real running run always has a
                // heartbeating claimant, and the host's orphan reaper (rightly) reclaims one that does not.
                await NodeStore.HeartbeatAsync(db, "cp-gstream-node-" + suffix, "1.0.0", DateTime.UtcNow, ct: ct);
                db.RunGroups.Add(new CatalogRunGroup
                {
                    GroupId = groupId, RepoId = repoId, Mode = "batch", Anchor = "BB", MemberCount = 2,
                    EnqueuedUtc = t0,
                });
                var runningMember = SeedMember(runA, repoId, flowA, groupId, groupWave: 1, RunStatuses.Running, t0);
                runningMember.ClaimedByNode = "cp-gstream-node-" + suffix;
                db.Runs.Add(runningMember);
                db.Runs.Add(SeedMember(runB, repoId, flowB, groupId, groupWave: 2, RunStatuses.Queued, t0));
                await db.SaveChangesAsync(ct);
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            using var missing = await SendStreamAsync(client, token, $"/api/v1/runs/groups/{Guid.NewGuid()}/stream", ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using var response = await SendStreamAsync(client, token, $"/api/v1/runs/groups/{groupId}/stream", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var frames = new SseFrameReader(body);

            // The connect snapshot: every member, in execution order (group wave), with no last action yet.
            var first = Deserialize<RunSummaryDto>(await frames.ReadAsync("member", ct));
            Assert.Equal(runA, first.RunId);
            Assert.Equal(RunStatuses.Running, first.Status);
            Assert.Null(first.LastAction);
            var second = Deserialize<RunSummaryDto>(await frames.ReadAsync("member", ct));
            Assert.Equal(runB, second.RunId);
            Assert.Equal(RunStatuses.Queued, second.Status);

            // A trace event landing for the running member republishes it with the new last action.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = runA, RepoId = repoId, Ordinal = 1, TimestampUtc = t0.AddSeconds(5), Level = "info",
                    Step = "source.open", Message = "read 'a.csv' (31 row(s))", Rows = 31,
                });
                await db.SaveChangesAsync(ct);
            }

            var acting = Deserialize<RunSummaryDto>(await frames.ReadAsync("member", ct));
            Assert.Equal(runA, acting.RunId);
            Assert.Equal("read 'a.csv' (31 row(s))", acting.LastAction);
            Assert.NotNull(acting.LastActionUtc);

            // Every member reaching a terminal state republishes the changed rows, then ends the stream with
            // the final rollup.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.GroupId == groupId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Succeeded), ct);
            }

            var finalA = Deserialize<RunSummaryDto>(await frames.ReadAsync("member", ct));
            Assert.Equal(runA, finalA.RunId);
            Assert.Equal(RunStatuses.Succeeded, finalA.Status);
            var finalB = Deserialize<RunSummaryDto>(await frames.ReadAsync("member", ct));
            Assert.Equal(runB, finalB.RunId);
            Assert.Equal(RunStatuses.Succeeded, finalB.Status);

            var end = Deserialize<RunGroupCountsDto>(await frames.ReadAsync("end", ct));
            Assert.Equal(2, end.Total);
            Assert.Equal(2, end.Succeeded);
            Assert.True(await frames.AtEndAsync(ct), "the stream must close after the end frame");
        }
        finally
        {
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            await using var db = CatalogDatabase.Create(cs);
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.GroupId == groupId).ExecuteDeleteAsync();
            await NodeStore.DeleteAsync(db, "cp-gstream-node-" + suffix);
        }
    }

    private static CatalogRun SeedMember(
        Guid runId, Guid repoId, string flowName, Guid groupId, int groupWave, string status, DateTime t0)
        => new()
        {
            RunId = runId,
            PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "ing",
            Status = status,
            Success = false,
            GroupId = groupId,
            GroupWave = groupWave,
            EnqueuedUtc = t0,
            WrittenUtc = t0,
        };

    private static T Deserialize<T>(string data)
    {
        var value = JsonSerializer.Deserialize<T>(data, JsonWeb.Options);
        Assert.NotNull(value);
        return value;
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
