using System.Net;
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
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A registered flow kind's run arguments through the control plane: the kinds listing, a trigger carrying an
/// operation, values and a payload (validated by the pipeline's kind, recorded on the run with who asked, served by the
/// run detail and handed to the node), a schedule passing its operation to the members whose kind declares it, and a
/// schedule refused when no member would take its operation. The kind is the test-only <c>ops</c> kind, registered in the
/// host the way a module registers its own.
/// </summary>
public sealed class RunKindArgumentsApiTests
{
    private const string Node = "kind-args-node";

    private static ControlPlaneAppFactory Host(string? catalog = null)
    {
        var factory = new ControlPlaneAppFactory()
            .WithServices(services => services.AddSingleton<IFlowDocumentKind, OpsFlowKind>());
        return catalog is null ? factory : factory.WithCatalog(catalog);
    }

    [Fact]
    public async Task Kinds_ListsTheRegisteredKind_WithItsOperationsAndDefault()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/kinds", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var kinds = await response.Content.ReadFromJsonAsync<List<FlowKindDto>>();
        var kind = Assert.Single(kinds!);
        Assert.Equal("ops", kind.FlowType);
        Assert.Equal(["send", "verify"], kind.Operations.Select(o => o.Name));
        Assert.Equal("send", kind.DefaultOperation);
        Assert.False(kind.Operations[1].WritesTarget);
    }

    [Fact]
    public async Task Trigger_ANodeScopeCarryingAnOperation_IsRefused()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(
            client, token, "/api/v1/runs",
            new RunTriggerRequest(Guid.NewGuid(), "anchor", Scope: "node", Operation: "verify"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("apply to a single flow", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Trigger_ARegisteredKindsFlow_RecordsItsArgumentsAndRequester_ServesThem_AndHandsThemToTheNode()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flowName = "ops_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flowName, "ops"));
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostAsync(client, token, "/api/v1/runs", new RunTriggerRequest(
                repoId, flowName, Operation: "verify",
                Values: new Dictionary<string, string> { ["region"] = "north" },
                Payload: JsonDocument.Parse("""{ "scope" : "all" }""").RootElement));

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<RunTriggerAccepted>())!;

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == accepted.RunId);
                Assert.Equal("verify", run.Operation);
                Assert.Equal("""{"region":"north"}""", run.ValuesJson);
                Assert.Equal("""{"scope":"all"}""", run.Payload);
                Assert.False(string.IsNullOrWhiteSpace(run.RequestedBy));

                var spec = await RunQueueStore.MarkHandedOutAsync(db, accepted.RunId, 0, Node, DateTime.UtcNow);
                Assert.NotNull(spec);
                Assert.Equal("verify", spec.Parameters.Operation);
                Assert.Equal("north", spec.Parameters.Values["region"]);
                Assert.Equal("""{"scope":"all"}""", spec.Parameters.Payload);
                Assert.Equal(run.RequestedBy, spec.RequestedBy);
            }

            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{accepted.RunId}", UriKind.Relative));
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var detailResponse = await client.SendAsync(detailRequest);
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
            Assert.Equal("verify", detail.RootElement.GetProperty("operation").GetString());
            Assert.Equal("north", detail.RootElement.GetProperty("values").GetProperty("region").GetString());
            Assert.Equal("all", detail.RootElement.GetProperty("payload").GetProperty("scope").GetString());
            Assert.False(string.IsNullOrWhiteSpace(detail.RootElement.GetProperty("requestedBy").GetString()));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableTheory]
    [Trait("Category", "Integration")]
    [InlineData("ing", "verify", null, "'ing' flows take none")]
    [InlineData("ops", "purge", null, "operation must be one of send, verify for 'ops' flows")]
    [InlineData("ops", "send", """{"other":1}""", "payload must name a 'scope'")]
    public async Task Trigger_RefusesArgumentsTheFlowsKindDoesNotTake(string kind, string operation, string? payload, string expected)
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flowName = "refused_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (flowName, kind));
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostAsync(client, token, "/api/v1/runs", new RunTriggerRequest(
                repoId, flowName, Operation: operation,
                Payload: payload is null ? null : JsonDocument.Parse(payload).RootElement));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await using var db = CatalogDatabase.Create(cs);
            Assert.False(await db.Runs.AnyAsync(r => r.RepoId == repoId));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ScheduleFire_PassesItsOperationAndValues_OnlyToMembersWhoseKindDeclaresTheOperation()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string ops = "ops_" + suffix, ing = "ing_" + suffix;

        try
        {
            await SeedAsync(cs, repoId, (ops, "ops"), (ing, "ing"));
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;
            await ScheduleStore.StageYamlUpsertAsync(
                db, repoId, "nightly_" + suffix, [ops, ing], "0 4 * * *", null, "UTC", enabled: true, catchup: false,
                maxConcurrency: null, now.AddHours(1), now, operation: "verify",
                values: new Dictionary<string, string> { ["region"] = "north" });
            await db.SaveChangesAsync();
            var schedule = await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId);

            var fire = await ScheduleFire.EnqueueAsync(
                db, new StoreDispatcher(), YamlDocumentLoader.CreateDefault([new OpsFlowKind()]), schedule, now, default);

            Assert.Equal(ScheduleFire.Outcome.EnqueuedGroup, fire.Outcome);
            var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
            var opsRun = Assert.Single(runs, r => r.FlowName == ops);
            Assert.Equal("verify", opsRun.Operation);
            Assert.Equal("""{"region":"north"}""", opsRun.ValuesJson);
            var ingRun = Assert.Single(runs, r => r.FlowName == ing);
            Assert.Null(ingRun.Operation);
            Assert.Null(ingRun.ValuesJson);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CreateSchedule_ValidatesItsOperationAgainstTheMembersKinds()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string ops = "ops_" + suffix, ing = "ing_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, (ops, "ops"), (ing, "ing"));
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using (var refused = await PostAsync(client, token, "/api/v1/schedules", new CreateScheduleRequest(
                       repoId, [ing], "0 6 1 1 *", null, "UTC", true, Name: "refused_" + suffix, Operation: "verify")))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains("declares the operation 'verify'", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var created = await PostAsync(client, token, "/api/v1/schedules", new CreateScheduleRequest(
                       repoId, [ops, ing], "0 6 1 1 *", null, "UTC", true, Name: "mixed_" + suffix, Operation: "verify",
                       Values: new Dictionary<string, string> { ["region"] = "north" })))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            await using var db = CatalogDatabase.Create(cs);
            var schedule = await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId);
            Assert.Equal("verify", schedule.Operation);
            Assert.Equal("""{"region":"north"}""", schedule.ValuesJson);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("kindargs_" + suffix), suffix);
    }

    private static async Task SeedAsync(string cs, Guid repoId, params (string Name, string Kind)[] pipelines)
    {
        await using var db = CatalogDatabase.Create(cs);
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "kindargs_" + repoId.ToString("N")[..8],
            RemoteUrl = "https://git.invalid/kindargs.git",
            RootPath = Path.Combine(Path.GetTempPath(), "kindargs_" + repoId.ToString("N")[..8]),
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        var wave = 0;
        foreach (var (name, kind) in pipelines)
        {
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, name),
                RepoId = repoId,
                Name = name,
                Kind = kind,
                RelativePath = $"flows/{name}.yaml",
                Active = true,
                Wave = wave++,
                ExecutionMode = PipelineExecutionModes.Auto,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
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

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string token, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>A dispatcher that enqueues through the real store, without the hosted worker's signalling.</summary>
    private sealed class StoreDispatcher : IRunDispatcher
    {
        public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
            => (await RunQueueStore.EnqueueAsync(catalog, request, DateTime.UtcNow, ct)).RunId;

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(
            CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
            => RunQueueStore.EnqueueGroupAsync(catalog, request, DateTime.UtcNow, ct);

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A test-only kind with two operations whose payload must name a <c>scope</c>.</summary>
    private sealed class OpsFlowKind : IFlowDocumentKind
    {
        public string FlowType => "ops";

        public string Description => "a test-only flow with operations";

        public IReadOnlyList<FlowKindOperation> Operations { get; } =
        [
            new("send", "Send", "Sends the flow's records.", WritesTarget: true),
            new("verify", "Verify", "Reads the records back.", WritesTarget: false),
        ];

        public RegisteredFlowDocument Parse(string yaml, string source)
            => throw new FlowValidationException($"{source}: the ops kind parses no documents in these tests.");

        public void ValidateParameters(RunParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (parameters.Payload is null)
            {
                return;
            }

            using var payload = JsonDocument.Parse(parameters.Payload);
            if (!payload.RootElement.TryGetProperty("scope", out _))
            {
                throw new SqlFlowException("payload must name a 'scope'.");
            }
        }
    }
}
