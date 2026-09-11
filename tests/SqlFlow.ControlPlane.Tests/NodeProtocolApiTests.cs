using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Identity;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The node protocol end to end through the in-memory host and a real <see cref="HttpNodeTransport"/>: a node
/// polls for work, is handed a run the API enqueued, reports its outcome under the fence (and a stale report is
/// dropped), hears an operator's cancel on its next poll, loses a lease it stops renewing (and a successor takes
/// the run at the next attempt), and is refused without the node scope or while the replica's dispatcher is not
/// the owner (503 with a retry hint, retried automatically once ownership returns). The host's own in-process
/// node is switched off so the test is the only node. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NodeProtocolApiTests
{
    private const string NodeName = "proto-node";

    [SkippableFact]
    public async Task Poll_HandsOutAnEnqueuedRun_AndTheOutcomeIsRecordedUnderTheFence()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            using var factory = NewFactory(cs);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["node", "read"]);
            using var transport = NewTransport(factory, token);
            await WaitForActiveDispatcherAsync(client, token);

            // Nothing queued: an immediate poll answers empty.
            var idle = await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None);
            Assert.False(idle.HasContent);

            var runId = await EnqueueAsync(factory, cs, repoId, flowName);
            var handed = await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None);
            var handout = Assert.Single(handed.Runs);
            Assert.Equal((runId, 1), (handout.RunId, handout.Attempt));
            await using (var db = CatalogDatabase.Create(cs))
            {
                var row = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal((RunStatuses.Running, NodeName, 1), (row.Status, row.ClaimedByNode, row.Attempt));
            }

            // A report at the wrong attempt is dropped; the right one records the artifact's outcome.
            Assert.Equal(RunOutcomeStatus.StaleClaim, await transport.ReportRunOutcomeAsync(
                runId, new RunOutcomeRequest(NodeName, 2, RunOutcomeKind.Completed, null, Artifact(runId, flowName)), CancellationToken.None));
            Assert.Equal(RunOutcomeStatus.Recorded, await transport.ReportRunOutcomeAsync(
                runId, new RunOutcomeRequest(NodeName, 1, RunOutcomeKind.Completed, null, Artifact(runId, flowName)), CancellationToken.None));
            await using (var db = CatalogDatabase.Create(cs))
            {
                var row = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal((RunStatuses.Succeeded, 7L), (row.Status, row.RowsLoaded));
            }

            // The dispatch read surface shows the node and an empty queue.
            var snapshot = await ReadSnapshotAsync(client, token);
            Assert.True(snapshot.Active);
            Assert.Contains(snapshot.Nodes, n => n.Name == NodeName && n.Online);
            Assert.Empty(snapshot.LeasedRuns);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Cancel_ReachesTheHoldingNodeOnItsNextPoll_AndTheCancelledOutcomeIsRecorded()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            using var factory = NewFactory(cs);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["node", "operate"]);
            using var transport = NewTransport(factory, token);
            await WaitForActiveDispatcherAsync(client, token);

            var runId = await EnqueueAsync(factory, cs, repoId, flowName);
            var handout = Assert.Single((await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None)).Runs);

            // The operator cancels through the API; the node's poll (parked, no free slots) answers at once.
            var parked = transport.PollAsync(Poll(freeRuns: 0, holding: [new HeldRun(runId, handout.Attempt)], waitSeconds: 5), CancellationToken.None);
            using var cancel = await client.SendAsync(Authorized(HttpMethod.Post, $"/api/v1/runs/{runId}/cancel", token));
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
            var woken = await parked.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([runId], woken.CancelRuns);

            Assert.Equal(RunOutcomeStatus.Recorded, await transport.ReportRunOutcomeAsync(
                runId, new RunOutcomeRequest(NodeName, handout.Attempt, RunOutcomeKind.Cancelled, null, null), CancellationToken.None));
            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(RunStatuses.Cancelled, (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId)).Status);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ALeaseTheNodeStopsRenewing_IsRevoked_AndASuccessorTakesTheRunAtTheNextAttempt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            using var factory = NewFactory(cs);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["node", "read"]);
            using var transport = NewTransport(factory, token);
            await WaitForActiveDispatcherAsync(client, token);

            var runId = await EnqueueAsync(factory, cs, repoId, flowName);
            var handout = Assert.Single((await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None)).Runs);

            // Silence past the 2-second lease: the dispatcher requeues the run and tells the zombie so.
            await WaitUntilAsync(async () =>
            {
                await using var db = CatalogDatabase.Create(cs);
                return (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId)).Status == RunStatuses.Queued;
            }, TimeSpan.FromSeconds(15));
            var zombie = await transport.PollAsync(Poll(freeRuns: 0, holding: [new HeldRun(runId, handout.Attempt)]), CancellationToken.None);
            Assert.Equal([runId], zombie.RevokedRuns);

            var successor = Assert.Single((await transport.PollAsync(Poll(freeRuns: 1, node: "proto-successor"), CancellationToken.None)).Runs);
            Assert.Equal((runId, 2), (successor.RunId, successor.Attempt));
            Assert.Equal(RunOutcomeStatus.StaleClaim, await transport.ReportRunOutcomeAsync(
                runId, new RunOutcomeRequest(NodeName, 1, RunOutcomeKind.Completed, null, Artifact(runId, flowName)), CancellationToken.None));
            Assert.Equal(RunOutcomeStatus.Recorded, await transport.ReportRunOutcomeAsync(
                runId, new RunOutcomeRequest("proto-successor", 2, RunOutcomeKind.Completed, null, Artifact(runId, flowName)), CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task APassiveReplica_Answers503WithRetryAfter_AndServesAgainOnceItOwnsDispatch()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        using var factory = NewFactory(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["node", "read"]);
        using var transport = NewTransport(factory, token);
        await WaitForActiveDispatcherAsync(client, token);

        factory.Services.GetRequiredService<Dispatcher>().Deactivate();
        var refused = await Assert.ThrowsAsync<NodeTransportException>(() => transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None));
        Assert.Equal(503, refused.StatusCode);
        Assert.True(refused.Retryable);

        using var raw = await client.SendAsync(Authorized(HttpMethod.Post, NodeProtocol.RoutePrefix + "/poll", token, Poll(freeRuns: 1)));
        if (raw.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Equal("2", raw.Headers.RetryAfter?.ToString());
        }

        // The host's dispatch service still holds the ownership lease, so it re-activates within one renew interval.
        await WaitForActiveDispatcherAsync(client, token);
        Assert.False((await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None)).HasContent);
    }

    [Fact]
    public async Task NodeRoutes_RequireTheNodeScope()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        using var anonymous = await client.PostAsJsonAsync(new Uri(NodeProtocol.RoutePrefix + "/poll", UriKind.Relative), Poll(freeRuns: 1));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var operate = await IssueTokenAsync(client, ["operate", "read", "admin"]);
        using var forbidden = await client.SendAsync(Authorized(HttpMethod.Post, NodeProtocol.RoutePrefix + "/poll", operate, Poll(freeRuns: 1)));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // With the scope the request is authorized; on this database-less host the dispatcher is never the owner,
        // which is the 503 a node retries, never a 401/403.
        var node = await IssueTokenAsync(client, ["node"]);
        using var deferred = await client.SendAsync(Authorized(HttpMethod.Post, NodeProtocol.RoutePrefix + "/poll", node, Poll(freeRuns: 1)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, deferred.StatusCode);
        Assert.Equal("2", deferred.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Outcome_WithAnInvalidBody_Is400()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var node = await IssueTokenAsync(client, ["node"]);

        using var missingArtifact = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{NodeProtocol.RoutePrefix}/runs/{Guid.NewGuid()}/outcome", node,
            new RunOutcomeRequest(NodeName, 1, RunOutcomeKind.Completed, null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, missingArtifact.StatusCode);

        using var badAttempt = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{NodeProtocol.RoutePrefix}/runs/{Guid.NewGuid()}/outcome", node,
            new RunOutcomeRequest(NodeName, 0, RunOutcomeKind.Failed, "boom", null)));
        Assert.Equal(HttpStatusCode.BadRequest, badAttempt.StatusCode);

        var request = Authorized(HttpMethod.Post, $"{NodeProtocol.RoutePrefix}/runs/{Guid.NewGuid()}/outcome", node);
        request.Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        using var malformed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    private static ControlPlaneAppFactory NewFactory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            // This test is the only node; the host's in-process one would take the run first.
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            // Short lease and ownership windows so expiry and hand-over are observed in seconds, not minutes.
            .WithSetting("ControlPlane:Dispatch:LongPollSeconds", "1")
            .WithSetting("ControlPlane:Dispatch:LeaseSeconds", "2")
            .WithSetting("ControlPlane:Dispatch:ReconcileSeconds", "1")
            .WithSetting("ControlPlane:Dispatch:OwnershipRenewSeconds", "1")
            .WithSetting("ControlPlane:Dispatch:OwnershipTtlSeconds", "2");

    private static HttpNodeTransport NewTransport(ControlPlaneAppFactory factory, string token)
        => new(factory.Server.BaseAddress, token, factory.Server.CreateHandler());

    private static NodePollRequest Poll(int freeRuns, IReadOnlyList<HeldRun>? holding = null, int waitSeconds = 0, string node = NodeName)
        => new(node, "test", [], 4, freeRuns, 2, 0, holding ?? [], [], DateTime.UtcNow.AddMinutes(-1), waitSeconds);

    private static async Task<Guid> EnqueueAsync(ControlPlaneAppFactory factory, string cs, Guid repoId, string flowName)
    {
        // The production path: the API's dispatcher seam journals the row and tells the in-memory dispatcher.
        var dispatcher = factory.Services.GetRequiredService<IRunDispatcher>();
        await using var db = CatalogDatabase.Create(cs);
        return await dispatcher.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"));
    }

    /// <summary>Waits for this host's dispatcher to own dispatch. Normally that is immediate; after a previous owner
    /// died without releasing the lease (a crash, or a test harness that killed a child control plane) the successor
    /// waits out the lease's default 30-second TTL, which is exactly the recovery time the design promises.</summary>
    private static async Task WaitForActiveDispatcherAsync(HttpClient client, string token)
        => await WaitUntilAsync(async () => (await ReadSnapshotAsync(client, token)).Active, TimeSpan.FromSeconds(45));

    private static async Task<DispatchSnapshot> ReadSnapshotAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/dispatch", token));
        response.EnsureSuccessStatusCode();
        var snapshot = await response.Content.ReadFromJsonAsync<DispatchSnapshot>();
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition did not hold within the timeout.");
            }

            await Task.Delay(100);
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
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

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("np_" + suffix), "np_flow_" + suffix);
    }

    private static string Artifact(Guid runId, string flowName)
        => $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": true,
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": { "rowsLoaded": 7, "durationSeconds": 1.0 }
            }
            """;

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Nodes.Where(n => n.Name == NodeName || n.Name == "proto-successor").ExecuteDeleteAsync();
    }
}
