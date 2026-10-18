using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The values the control plane supplies with runs for their references: the registered sources merged in order and held
/// to a run's rules; every run, every member of a group and every compute task queued carrying them, unless its caller
/// composed its own; the values recorded on the run and handed to the node; a fan-out member resolving exactly as its
/// root; nothing attached when no source is registered; and a source supplying a written-out credential refusing the run
/// instead of queuing it. The host's in-process worker is off, so the tests read the queued rows themselves.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunReferencesTests
{
    private const string Node = "references-node";

    private static readonly Dictionary<string, string> Estate = new(StringComparer.Ordinal)
    {
        ["WAREHOUSE_DB"] = "Server=tcp:sql.example.net;Database=dw;Authentication=Active Directory Default",
        ["LANDING_URL"] = "abfss://landing@account.dfs.core.windows.net/drops/",
    };

    // ---- The sources -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoSource_SuppliesNothing()
    {
        var references = new RunReferences([], NullLogger<RunReferences>.Instance);

        Assert.False(references.HasSources);
        Assert.Empty(await references.ForAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Sources_AreMergedInRegistrationOrder_TheLaterWinning()
    {
        var repoId = Guid.NewGuid();
        var first = new FixedSource(new() { ["WAREHOUSE_DB"] = "first", ["LANDING_URL"] = "https://landing" });
        var second = new FixedSource(new() { ["WAREHOUSE_DB"] = "second" });
        var references = new RunReferences([first, second], NullLogger<RunReferences>.Instance);

        var supplied = await references.ForAsync(repoId, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["WAREHOUSE_DB"] = "second", ["LANDING_URL"] = "https://landing" }, supplied);
        Assert.Equal([(Guid?)repoId], first.AskedFor);
        Assert.Equal([(Guid?)repoId], second.AskedFor);
    }

    [Fact]
    public async Task ASourceSupplyingAWrittenOutCredential_IsRefused_NamingTheValue()
    {
        var references = new RunReferences([new FixedSource(new() { ["GIT_TOKEN"] = "ghp_abc" })], NullLogger<RunReferences>.Instance);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => references.ForAsync(null, CancellationToken.None));

        Assert.StartsWith("The configuration the control plane supplies with runs give GIT_TOKEN a value that is not a reference", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_abc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceAnsweringNull_IsAnError()
    {
        var references = new RunReferences([new NullSource()], NullLogger<RunReferences>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => references.ForAsync(null, CancellationToken.None));
    }

    // ---- Through the dispatcher ------------------------------------------------------------------------------------

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ATriggeredRun_IsQueuedCarryingTheRepositorysValues_AndHandsThemToTheNode()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flow) = NewIds();
        var source = new FixedSource(Estate);

        await using var factory = Host(cs, source);
        try
        {
            await SeedAsync(cs, repoId, flow);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostAsync(client, token, "/api/v1/runs", new RunTriggerRequest(repoId, flow));

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<RunTriggerAccepted>())!;
            Assert.Contains(repoId, source.AskedFor);
            await using var db = CatalogDatabase.Create(cs);
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == accepted.RunId);
            Assert.Equal(SuppliedReferences.ToJson(Estate), run.ReferencesJson);
            var spec = await RunQueueStore.MarkHandedOutAsync(db, accepted.RunId, 0, Node, DateTime.UtcNow);
            Assert.NotNull(spec);
            Assert.Equal(Estate, spec.Parameters.References);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithNoSource_ARunIsQueuedCarryingNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flow) = NewIds();

        await using var factory = Host(cs, source: null);
        try
        {
            await SeedAsync(cs, repoId, flow);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostAsync(client, token, "/api/v1/runs", new RunTriggerRequest(repoId, flow));

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<RunTriggerAccepted>())!;
            await using var db = CatalogDatabase.Create(cs);
            Assert.Null(await db.Runs.AsNoTracking().Where(r => r.RunId == accepted.RunId).Select(r => r.ReferencesJson).SingleAsync());
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ARunTheConfigurationCannotSupplyRightly_IsRefused_AndNothingIsQueued()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flow) = NewIds();

        await using var factory = Host(cs, new FixedSource(new() { ["WAREHOUSE_DB"] = "Server=sql;Password=hunter2" }));
        try
        {
            await SeedAsync(cs, repoId, flow);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostAsync(client, token, "/api/v1/runs", new RunTriggerRequest(repoId, flow));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("give WAREHOUSE_DB a connection string with a password, secret or key written out", body, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
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
    public async Task EveryMemberOfAGroup_CarriesTheValues_UnlessItsCallerComposedItsOwn()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flow) = NewIds();
        string first = flow + "_a", second = flow + "_b";
        var own = new Dictionary<string, string>(StringComparer.Ordinal) { ["WAREHOUSE_DB"] = "${env:WAREHOUSE_DB}" };

        await using var factory = Host(cs, new FixedSource(Estate));
        try
        {
            await SeedAsync(cs, repoId, first, second);
            await using var db = CatalogDatabase.Create(cs);
            var dispatcher = factory.Services.GetRequiredService<IRunDispatcher>();

            var group = await dispatcher.EnqueueGroupAsync(db, new RunGroupEnqueueRequest(
                repoId, RunGroupModes.Batch, first,
                [new RunScopeMember(first, "ing", 0, string.Empty), new RunScopeMember(second, "ing", 1, string.Empty)],
                MemberParameters: new Dictionary<string, RunParameters>(StringComparer.Ordinal)
                {
                    [second] = new RunParameters { FullLoad = true, References = own },
                }));

            var rows = await db.Runs.AsNoTracking().Where(r => r.GroupId == group.GroupId).ToListAsync();
            Assert.Equal(SuppliedReferences.ToJson(Estate), Assert.Single(rows, r => r.FlowName == first).ReferencesJson);
            var composed = Assert.Single(rows, r => r.FlowName == second);
            Assert.Equal(SuppliedReferences.ToJson(own), composed.ReferencesJson);
            Assert.True(composed.FullLoad);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AComputeTask_IsQueuedCarryingTheValuesForWorkOfNoRepository_UnlessItNamesItsOwn()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var source = new FixedSource(Estate);

        await using var factory = Host(cs, source);
        var taskIds = new List<Guid>();
        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var dispatcher = factory.Services.GetRequiredService<IRunDispatcher>();
            var plain = new ComputeTaskPayload { Operation = ComputeOperations.TestConnection, SourceRef = "${env:WAREHOUSE_DB}" };
            var composed = plain with { References = new Dictionary<string, string>(StringComparer.Ordinal) { ["WAREHOUSE_DB"] = "own" } };

            taskIds.Add(await dispatcher.EnqueueComputeTaskAsync(db, new ComputeTaskEnqueueRequest(plain.Operation, plain.SourceRef, null, plain.ToJson())));
            taskIds.Add(await dispatcher.EnqueueComputeTaskAsync(db, new ComputeTaskEnqueueRequest(composed.Operation, composed.SourceRef, null, composed.ToJson())));

            var rows = await db.ComputeTasks.AsNoTracking().Where(t => taskIds.Contains(t.TaskId)).ToDictionaryAsync(t => t.TaskId, t => t.ArgumentsJson);
            Assert.Equal(Estate, ComputeTaskPayload.FromJson(rows[taskIds[0]]).References);
            Assert.Equal(composed.References, ComputeTaskPayload.FromJson(rows[taskIds[1]]).References);
            Assert.Contains(null, source.AskedFor);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ComputeTasks.Where(t => taskIds.Contains(t.TaskId)).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AFanOutMember_ResolvesExactlyAsItsRoot()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flow) = NewIds();

        try
        {
            await SeedAsync(cs, repoId, flow);
            await using var db = CatalogDatabase.Create(cs);
            var root = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, flow, "fan", Parameters: new RunParameters { References = Estate }), DateTime.UtcNow);
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, root.RunId, 0, Node, DateTime.UtcNow));

            // The node's members name references of their own; the control plane gives each the root's instead.
            var stray = new Dictionary<string, string>(StringComparer.Ordinal) { ["WAREHOUSE_DB"] = "stray" };
            var fanOut = await RunQueueStore.EnqueueFanOutAsync(
                db, root.RunId, Node, 1,
                [new RunParameters { Operation = "load", Payload = """{"slice":1}""", References = stray }, new RunParameters { Operation = "load", Payload = """{"slice":2}""" }],
                DateTime.UtcNow);

            Assert.True(fanOut.Held);
            var members = await db.Runs.AsNoTracking().Where(r => r.FanOutRoot == root.RunId).Select(r => r.ReferencesJson).ToListAsync();
            Assert.Equal(2, members.Count);
            Assert.All(members, json => Assert.Equal(SuppliedReferences.ToJson(Estate), json));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    private static ControlPlaneAppFactory Host(string cs, IRunReferenceSource? source)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithServices(services =>
            {
                if (source is not null)
                {
                    services.AddSingleton(source);
                }
            });

    private static (Guid RepoId, string Flow) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("references_" + suffix), "refs_" + suffix);
    }

    private static async Task SeedAsync(string cs, Guid repoId, params string[] flows)
    {
        await using var db = CatalogDatabase.Create(cs);
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "references_" + repoId.ToString("N")[..8],
            RemoteUrl = "https://git.invalid/references.git",
            RootPath = Path.Combine(Path.GetTempPath(), "references_" + repoId.ToString("N")[..8]),
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        var wave = 0;
        foreach (var flow in flows)
        {
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, flow),
                RepoId = repoId,
                Name = flow,
                Kind = "ing",
                RelativePath = $"flows/{flow}.yaml",
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

    /// <summary>A source that supplies the same values for every repository and records what it was asked for.</summary>
    private sealed class FixedSource(Dictionary<string, string> values) : IRunReferenceSource
    {
        private readonly Lock _gate = new();

        private readonly List<Guid?> _askedFor = [];

        /// <summary>Every repository it was asked for, in order. The host's scheduler and managed sync share the test
        /// database with other tests, so a test asserts what it caused, not that nothing else asked.</summary>
        public IReadOnlyList<Guid?> AskedFor
        {
            get
            {
                lock (_gate)
                {
                    return [.. _askedFor];
                }
            }
        }

        public Task<IReadOnlyDictionary<string, string>> ReferencesAsync(Guid? repoId, CancellationToken ct)
        {
            lock (_gate)
            {
                _askedFor.Add(repoId);
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(values);
        }
    }

    private sealed class NullSource : IRunReferenceSource
    {
        public Task<IReadOnlyDictionary<string, string>> ReferencesAsync(Guid? repoId, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(null!);
    }
}
