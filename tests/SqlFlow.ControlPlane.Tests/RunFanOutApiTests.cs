using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Node;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Fan-out run groups end to end through the in-memory host and a real <see cref="HttpNodeTransport"/>: a node holding a
/// run of a registered kind enqueues member runs of the same flow (fenced on its lease, rejoined on repeat, validated by
/// the kind), the members are handed out beside the root while another run of the flow waits, a member's result object
/// is recorded on its row and served by the run detail with its fan-out slot, the root reads its members back, and the
/// root's end cancels the members still running, after which the waiting run starts. A member cannot fan out again. The
/// host's own in-process node is switched off so the test is the only node. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunFanOutApiTests
{
    private const string NodeName = "fan-out-node";

    [SkippableFact]
    public async Task AFanOut_RunsBesideItsRoot_RecordsMemberResults_AndEndsWithItsRoot()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            using var factory = NewFactory(cs);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["node", "read", "operate"]);
            using var transport = new HttpNodeTransport(factory.Server.BaseAddress, token, factory.Server.CreateHandler());
            await WaitForActiveDispatcherAsync(client, token);

            var rootId = await EnqueueAsync(factory, cs, repoId, flowName);
            var root = Assert.Single((await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None)).Runs);
            Assert.Equal(rootId, root.RunId);
            var waiting = await EnqueueAsync(factory, cs, repoId, flowName);

            RunParameters[] members = [Slice(1), Slice(2)];
            Assert.False((await transport.EnqueueFanOutAsync(
                rootId, new FanOutRequest(NodeName, root.Attempt + 1, members), CancellationToken.None)).Held);
            var fanOut = await transport.EnqueueFanOutAsync(
                rootId, new FanOutRequest(NodeName, root.Attempt, members), CancellationToken.None);
            Assert.True(fanOut.Held);
            Assert.NotNull(fanOut.GroupId);
            Assert.Equal(2, fanOut.RunIds.Count);

            var again = await transport.EnqueueFanOutAsync(
                rootId, new FanOutRequest(NodeName, root.Attempt, members), CancellationToken.None);
            Assert.Equal(fanOut.GroupId, again.GroupId);
            Assert.Equal(fanOut.RunIds, again.RunIds);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var rows = await db.Runs.AsNoTracking()
                    .Where(r => r.FanOutRoot == rootId).OrderBy(r => r.FanOutSlot).ToListAsync();
                Assert.Equal(fanOut.RunIds, rows.Select(r => r.RunId));
                Assert.All(rows, r => Assert.Equal((flowName, "fan", 2, "load"), (r.FlowName, r.FlowKind, r.FanOutCount!.Value, r.Operation!)));
                Assert.Equal(["""{"slice":1}""", """{"slice":2}"""], rows.Select(r => r.Payload));
                var group = await db.RunGroups.AsNoTracking().SingleAsync(g => g.GroupId == fanOut.GroupId);
                Assert.Equal((RunGroupModes.FanOut, flowName, 2), (group.Mode, group.Anchor, group.MemberCount));
            }

            // Both members are handed out while the root runs; the other run of the flow is not.
            var handed = await transport.PollAsync(
                Poll(freeRuns: 3, holding: [new HeldRun(rootId, root.Attempt)]), CancellationToken.None);
            Assert.Equal(fanOut.RunIds.Order(), handed.Runs.Select(r => r.RunId).Order());
            Assert.Equal(["""{"slice":1}""", """{"slice":2}"""], handed.Runs.OrderBy(r => fanOut.RunIds.ToList().IndexOf(r.RunId)).Select(r => r.Spec.Parameters.Payload));
            var first = handed.Runs.Single(r => r.RunId == fanOut.RunIds[0]);
            var second = handed.Runs.Single(r => r.RunId == fanOut.RunIds[1]);

            Assert.Equal(RunOutcomeStatus.Recorded, await transport.ReportRunOutcomeAsync(
                first.RunId, new RunOutcomeRequest(NodeName, first.Attempt, RunOutcomeKind.Completed, null, Artifact(first.RunId, flowName)),
                CancellationToken.None));

            var state = await transport.GetFanOutStateAsync(
                rootId, fanOut.GroupId.Value, new FanOutFence(NodeName, root.Attempt), CancellationToken.None);
            Assert.True(state.Held);
            Assert.Equal(
                [(1, RunStatuses.Succeeded, """{"records":5}"""), (2, RunStatuses.Running, (string?)null)],
                state.Members.Select(m => (m.Slot, m.Status, m.ResultJson)));

            var detail = await ReadRunAsync(client, token, first.RunId);
            Assert.Equal((rootId, 1, 2), (detail.FanOutRoot!.Value, detail.FanOutSlot!.Value, detail.FanOutCount!.Value));
            Assert.Equal("""{"records":5}""", detail.ResultJson);

            // The root ends: the member still running hears the cancel, and once it has stopped the waiting run starts.
            Assert.Equal(RunOutcomeStatus.Recorded, await transport.ReportRunOutcomeAsync(
                rootId, new RunOutcomeRequest(NodeName, root.Attempt, RunOutcomeKind.Completed, null, Artifact(rootId, flowName)),
                CancellationToken.None));
            var heard = await transport.PollAsync(
                Poll(freeRuns: 0, holding: [new HeldRun(second.RunId, second.Attempt)]), CancellationToken.None);
            Assert.Equal([second.RunId], heard.CancelRuns);
            Assert.Equal(RunOutcomeStatus.Recorded, await transport.ReportRunOutcomeAsync(
                second.RunId, new RunOutcomeRequest(NodeName, second.Attempt, RunOutcomeKind.Cancelled, null, null), CancellationToken.None));

            var next = Assert.Single((await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None)).Runs);
            Assert.Equal(waiting, next.RunId);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task AFanOutTheKindRefuses_OrOneAskedForByAMember_IsRefusedAsABadRequest()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            using var factory = NewFactory(cs);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["node", "read", "operate"]);
            using var transport = new HttpNodeTransport(factory.Server.BaseAddress, token, factory.Server.CreateHandler());
            await WaitForActiveDispatcherAsync(client, token);

            var rootId = await EnqueueAsync(factory, cs, repoId, flowName);
            var root = Assert.Single((await transport.PollAsync(Poll(freeRuns: 1), CancellationToken.None)).Runs);

            var refused = await Assert.ThrowsAsync<NodeTransportException>(() => transport.EnqueueFanOutAsync(
                rootId,
                new FanOutRequest(NodeName, root.Attempt, [new RunParameters { Operation = "load", Payload = """{"part":1}""" }]),
                CancellationToken.None));
            Assert.Equal((400, false), (refused.StatusCode!.Value, refused.Retryable));
            Assert.Contains("fan-out member 1: payload must name a 'slice'.", refused.Message, StringComparison.Ordinal);

            var fanOut = await transport.EnqueueFanOutAsync(
                rootId, new FanOutRequest(NodeName, root.Attempt, [Slice(1)]), CancellationToken.None);
            var member = Assert.Single((await transport.PollAsync(
                Poll(freeRuns: 1, holding: [new HeldRun(rootId, root.Attempt)]), CancellationToken.None)).Runs);
            Assert.Equal(fanOut.RunIds[0], member.RunId);

            var nested = await Assert.ThrowsAsync<NodeTransportException>(() => transport.EnqueueFanOutAsync(
                member.RunId, new FanOutRequest(NodeName, member.Attempt, [Slice(9)]), CancellationToken.None));
            Assert.Equal(400, nested.StatusCode!.Value);
            Assert.Contains("is itself a fan-out member", nested.Message, StringComparison.Ordinal);

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(1, await db.Runs.CountAsync(r => r.FanOutRoot == rootId));
            Assert.False(await db.Runs.AnyAsync(r => r.FanOutRoot == member.RunId));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    private static RunParameters Slice(int slice)
        => new() { Operation = "load", Payload = $$"""{"slice":{{slice}}}""" };

    private static ControlPlaneAppFactory NewFactory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            // This test is the only node; the host's in-process one would take the runs first.
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("ControlPlane:Dispatch:LongPollSeconds", "1")
            .WithSetting("ControlPlane:Dispatch:ReconcileSeconds", "1")
            .WithServices(services => services.AddSingleton<IFlowDocumentKind, FanFlowKind>());

    private static NodePollRequest Poll(int freeRuns, IReadOnlyList<HeldRun>? holding = null)
        => new(NodeName, "test", [], 4, freeRuns, 2, 0, holding ?? [], [], DateTime.UtcNow.AddMinutes(-1), 0);

    private static async Task<Guid> EnqueueAsync(ControlPlaneAppFactory factory, string cs, Guid repoId, string flowName)
    {
        var dispatcher = factory.Services.GetRequiredService<IRunDispatcher>();
        await using var db = CatalogDatabase.Create(cs);
        return await dispatcher.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "fan"));
    }

    private static async Task WaitForActiveDispatcherAsync(HttpClient client, string token)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            using var request = Authorized(HttpMethod.Get, "/api/v1/dispatch", token);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            if ((await response.Content.ReadFromJsonAsync<DispatchSnapshot>())!.Active)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the host's dispatcher did not take ownership within the timeout.");
            }

            await Task.Delay(100);
        }
    }

    private static async Task<RunDetailDto> ReadRunAsync(HttpClient client, string token, Guid runId)
    {
        using var request = Authorized(HttpMethod.Get, $"/api/v1/runs/{runId}", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RunDetailDto>())!;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("fo_" + suffix), "fo_flow_" + suffix);
    }

    private static string Artifact(Guid runId, string flowName)
        => $$"""
            {
              "schemaVersion": 1,
              "flowKind": "fan",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": true,
              "writtenUtc": "2026-09-15T10:00:00Z",
              "result": { "records": 5 }
            }
            """;

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Nodes.Where(n => n.Name == NodeName).ExecuteDeleteAsync();
    }

    /// <summary>A test-only kind with one operation whose payload must name a <c>slice</c>; its results are recorded on
    /// the run row because it is registered.</summary>
    private sealed class FanFlowKind : IFlowDocumentKind
    {
        public string FlowType => "fan";

        public string Description => "a test-only flow that fans out";

        public IReadOnlyList<FlowKindOperation> Operations { get; } =
            [new("load", "Load", "Loads one slice.", WritesTarget: true)];

        public RegisteredFlowDocument Parse(string yaml, string source)
            => throw new FlowValidationException($"{source}: the fan kind parses no documents in these tests.");

        public void ValidateParameters(RunParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (parameters.Payload is null)
            {
                return;
            }

            using var payload = JsonDocument.Parse(parameters.Payload);
            if (!payload.RootElement.TryGetProperty("slice", out _))
            {
                throw new SqlFlowException("payload must name a 'slice'.");
            }
        }
    }
}
