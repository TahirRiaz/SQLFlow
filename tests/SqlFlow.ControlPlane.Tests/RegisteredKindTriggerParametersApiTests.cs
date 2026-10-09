using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The built-in overrides on a trigger (a full load, a backfill window, a file pattern, a source filter) reach the check
/// of a registered kind whether or not the trigger carries kind arguments, so a flow of a kind that does not apply them
/// is refused with the kind's own message and nothing is queued, rather than queued to run as defined. A node scope
/// takes only a backfill window, and the window its anchor would take passes the anchor's kind first. The kind is the
/// test-only <c>strict</c> kind, which applies none of the overrides.
/// </summary>
public sealed class RegisteredKindTriggerParametersApiTests
{
    // The host's in-process worker is off: nothing here should run, and a hosted worker would claim what a refusal
    // failed to stop.
    private static ControlPlaneAppFactory Host(string? catalog = null)
    {
        var factory = new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithServices(services => services.AddSingleton<IFlowDocumentKind, StrictFlowKind>());
        return catalog is null ? factory : factory.WithCatalog(catalog);
    }

    public static TheoryData<string, bool, DateTime?, DateTime?, string?, string?> Overrides => new()
    {
        { "fullLoad", true, null, null, null, null },
        { "a backfill window", false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), null, null },
        { "filePattern", false, null, null, "parcels_*.csv", null },
        { "sourceFilter", false, null, null, null, "AND id > 10" },
    };

    [SkippableTheory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Overrides))]
    public async Task Trigger_AnOverrideTheFlowsKindDoesNotApply_IsRefused_AndQueuesNothing(
        string named, bool fullLoad, DateTime? from, DateTime? to, string? filePattern, string? sourceFilter)
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flowName = "strict_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, flowName);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            using var response = await PostAsync(client, token, new RunTriggerRequest(
                repoId, flowName, FullLoad: fullLoad, BackfillFrom: from, BackfillTo: to, FilePattern: filePattern,
                SourceFilter: sourceFilter));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"{named} does not apply to 'strict' flows", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
    public async Task Trigger_ARegisteredKindsFlow_WithoutOverrides_IsQueued()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flowName = "strict_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, flowName);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            using var response = await PostAsync(client, token, new RunTriggerRequest(repoId, flowName));

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = (await response.Content.ReadFromJsonAsync<RunTriggerAccepted>())!;
            await using var db = CatalogDatabase.Create(cs);
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == accepted.RunId);
            Assert.False(run.FullLoad);
            Assert.Null(run.BackfillFrom);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Trigger_ANodeBackfillAnchoredOnARegisteredKind_IsRefusedNamingTheAnchor_AndQueuesNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var flowName = "strict_" + suffix;

        await using var factory = Host(cs);
        try
        {
            await SeedAsync(cs, repoId, flowName);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client);

            using var response = await PostAsync(client, token, new RunTriggerRequest(
                repoId, flowName, Scope: "node",
                BackfillFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                BackfillTo: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"'{flowName}': a backfill window does not apply to 'strict' flows", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await using var db = CatalogDatabase.Create(cs);
            Assert.False(await db.Runs.AnyAsync(r => r.RepoId == repoId));
            Assert.False(await db.RunGroups.AnyAsync(g => g.RepoId == repoId));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [Theory]
    [InlineData("fullLoad", true, null, null)]
    [InlineData("filePattern", false, "orders_*.csv", null)]
    [InlineData("sourceFilter", false, null, "AND id > 10")]
    public async Task Trigger_ANodeScopeCarryingASingleFlowOverride_IsRefused(
        string named, bool fullLoad, string? filePattern, string? sourceFilter)
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client);

        using var response = await PostAsync(client, token, new RunTriggerRequest(
            Guid.NewGuid(), "anchor", Scope: "node", FullLoad: fullLoad, FilePattern: filePattern, SourceFilter: sourceFilter));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"{named} apply to a single flow; a node scope takes only a backfill window", body, StringComparison.Ordinal);
    }

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("strictkind_" + suffix), suffix);
    }

    private static async Task SeedAsync(string cs, Guid repoId, string flowName)
    {
        await using var db = CatalogDatabase.Create(cs);
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "strictkind_" + repoId.ToString("N")[..8],
            RemoteUrl = "https://git.invalid/strictkind.git",
            RootPath = Path.Combine(Path.GetTempPath(), "strictkind_" + repoId.ToString("N")[..8]),
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "strict",
            RelativePath = $"flows/{flowName}.yaml",
            Active = true,
            Wave = 0,
            ExecutionMode = PipelineExecutionModes.Auto,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
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

    private static async Task<string> IssueTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, RunTriggerRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/runs", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>A test-only kind with one implicit operation that applies none of the built-in overrides.</summary>
    private sealed class StrictFlowKind : IFlowDocumentKind
    {
        public string FlowType => "strict";

        public string Description => "a test-only flow that reads its whole source every run";

        public IReadOnlyList<FlowKindOperation> Operations => [];

        public RegisteredFlowDocument Parse(string yaml, string source)
            => throw new FlowValidationException($"{source}: the strict kind parses no documents in these tests.");

        public void ValidateParameters(RunParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var named = new List<string>();
            if (parameters.FullLoad)
            {
                named.Add("fullLoad");
            }

            if (parameters.BackfillFrom is not null || parameters.BackfillTo is not null)
            {
                named.Add("a backfill window");
            }

            if (parameters.FilePattern is not null)
            {
                named.Add("filePattern");
            }

            if (parameters.SourceFilter is not null)
            {
                named.Add("sourceFilter");
            }

            if (parameters.ReprocessFromSourceMin)
            {
                named.Add("reprocessFromSourceMin");
            }

            if (named.Count > 0)
            {
                throw new SqlFlowException(
                    $"{string.Join(", ", named)} does not apply to 'strict' flows; a strict flow reads its whole source on every run.");
            }
        }
    }
}
