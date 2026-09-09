using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Cli.Remote;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The CLI's control-plane client (<see cref="ControlPlaneClient"/>) end to end against the in-memory host:
/// the exact classes 'sqlflow login/trigger/runs/groups' run on, driven through the same HTTP surface the GUI
/// uses. The no-database tests cover health probing, the ProblemDetails-to-error mapping (with its sign-in
/// guidance), and the SSE frame parser against a canned stream. The DB-backed journey covers the full
/// credential and execution loop: password login, PAT minting, PAT-authenticated repo resolution, run
/// trigger, run detail and listing, lifecycle-honoring cancel, and revocation locking the client out.
/// This file deliberately does not import SqlFlow.ControlPlane.Api: the CLI's own mirror records are the
/// contract under test, so a server-side rename that breaks the CLI breaks here.
/// </summary>
public sealed class CliRemoteClientTests
{
    [Fact]
    public async Task HealthProbe_AgainstInMemoryHost_ReportsLive()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = new ControlPlaneClient(factory.CreateClient(), ownsClient: true);

        var (status, _) = await client.ProbeHealthAsync("/health/live", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task UnauthenticatedCall_SurfacesA401WithSignInGuidance()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = new ControlPlaneClient(factory.CreateClient(), ownsClient: true);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => client.ListRunsAsync(null, null, null, null, null, null, latest: false, page: 1, pageSize: 10, CancellationToken.None));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sqlflow login", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_ParsesEventsHeartbeatsAndMultiLineData()
    {
        // A canned text/event-stream exercising every frame shape the run/group streams emit: a heartbeat
        // comment (swallowed), a named event, a multi-line data frame (joined with \n, default event name),
        // and the terminal end event.
        const string wire =
            ": hb\n\n" +
            "event: entry\n" +
            "data: {\"id\":1,\"kind\":\"event\"}\n\n" +
            "data: {\"a\":\n" +
            "data: 2}\n\n" +
            "event: end\n" +
            "data: {\"status\":\"succeeded\"}\n\n";
        using var http = new HttpClient(new CannedSseHandler(wire)) { BaseAddress = new Uri("http://cli-tests.local") };
        using var client = new ControlPlaneClient(http, ownsClient: false);

        var events = new List<SseEvent>();
        await foreach (var sse in client.StreamAsync("/api/v1/runs/00000000-0000-0000-0000-000000000001/trace/stream", CancellationToken.None))
        {
            events.Add(sse);
        }

        Assert.Equal(3, events.Count);
        Assert.Equal("entry", events[0].Name);
        Assert.Equal("{\"id\":1,\"kind\":\"event\"}", events[0].Data);
        Assert.Equal("message", events[1].Name);
        Assert.Equal("{\"a\":\n2}", events[1].Data);
        Assert.Equal("end", events[2].Name);
        Assert.Equal("succeeded", ControlPlaneClient.ParseEvent<RunTraceStreamEndDto>(events[2]).Status);
    }

    [Fact]
    public void CredentialStore_NormalizesUrlVariantsToOneKey()
    {
        var canonical = CredentialStore.Normalize(new Uri("https://sqlflow.example.com"));

        Assert.Equal(canonical, CredentialStore.Normalize(new Uri("https://SQLFLOW.example.com/")));
        Assert.Equal(canonical, CredentialStore.Normalize(new Uri("https://sqlflow.example.com:443/")));
        Assert.NotEqual(canonical, CredentialStore.Normalize(new Uri("http://sqlflow.example.com")));
        Assert.NotEqual(canonical, CredentialStore.Normalize(new Uri("https://sqlflow.example.com:8443")));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CliJourney_LoginMintsPat_TriggersRun_ListsAndCancels_RevocationLocksOut()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cli_remote_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cli_remote_orders_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var username = "cli-remote-user-" + suffix;
        const string password = "a-long-cli-test-password-1";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await UserStore.EnsureRoleAsync(db, RoleNames.Operator, "read operate", "Operations.", now);
                var hasher = new PasswordHasher<CatalogUser>();
                var hash = hasher.HashPassword(new CatalogUser { Username = username }, password);
                var (createStatus, _) = await UserStore.CreateLocalAsync(db, username, hash, RoleNames.Operator, null, null, now);
                Assert.Equal(UserCreateStatus.Created, createStatus);

                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = "https://example/" + repoName + ".git",
                    RootPath = Path.Combine(Path.GetTempPath(), repoName),
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    RelativePath = "flows/" + flowName + ".flow.yaml",
                    ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
                    Yaml = $"name: {flowName}\nflowType: ing\n",
                    DefinitionJson = $$"""{"name":"{{flowName}}","flowType":"ing"}""",
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = new ControlPlaneClient(factory.CreateClient(), ownsClient: true);

            // Password sign-in: the CLI's default login path.
            var session = await client.LoginAsync(username, password, CancellationToken.None);
            Assert.Equal(username, session.Subject);
            Assert.Contains("operate", session.Scopes);

            // The session mints the PAT the CLI stores; the secret carries the sqlf_ shape the server routes on.
            client.BearerToken = session.AccessToken;
            var created = await client.CreateAccessTokenAsync("cli@tests", null, 1, CancellationToken.None);
            Assert.StartsWith("sqlf_", created.Secret, StringComparison.Ordinal);
            Assert.Contains("operate", created.Token.Scopes);

            // From here everything runs on the PAT, the credential a real CLI session persists.
            client.BearerToken = created.Secret;
            var tokens = await client.ListAccessTokensAsync(CancellationToken.None);
            Assert.Contains(tokens, t => t.Id == created.Token.Id && created.Secret.StartsWith(t.Prefix, StringComparison.Ordinal));

            var repos = await client.ListReposAsync(CancellationToken.None);
            var repo = Assert.Single(repos, r => r.Id == repoId);
            Assert.Equal(repoName, repo.Name);

            var outcome = await client.TriggerRunAsync(new RunTriggerRequest(repoId, flowName), CancellationToken.None);
            Assert.NotNull(outcome.Run);
            Assert.Null(outcome.Group);
            Assert.Equal("queued", outcome.Run.Status);

            var detail = await client.GetRunAsync(outcome.Run.RunId, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal(flowName, detail.FlowName);

            var listed = await client.ListRunsAsync(
                repoId, null, flowName, null, null, null, latest: false, page: 1, pageSize: 50, CancellationToken.None);
            Assert.Contains(listed.Items, r => r.RunId == outcome.Run.RunId);

            // The run races the host's own dispatcher, so any lifecycle-consistent answer is correct here;
            // what the client guarantees is the faithful mapping of the four contract statuses.
            var cancel = await client.CancelRunAsync(outcome.Run.RunId, CancellationToken.None);
            Assert.True(Enum.IsDefined(cancel));
            Assert.NotEqual(RemoteCancelOutcome.NotFound, cancel);

            // An unknown run is the one outcome that must always be NotFound.
            Assert.Equal(RemoteCancelOutcome.NotFound, await client.CancelRunAsync(Guid.NewGuid(), CancellationToken.None));
            Assert.Null(await client.GetRunAsync(Guid.NewGuid(), CancellationToken.None));

            // Revocation (what 'sqlflow logout' does) locks the credential out immediately.
            Assert.True(await client.RevokeAccessTokenAsync(created.Token.Id, CancellationToken.None));
            var locked = await Assert.ThrowsAsync<SqlFlowException>(
                () => client.ListAccessTokensAsync(CancellationToken.None));
            Assert.Contains("401", locked.Message, StringComparison.Ordinal);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Username == username).ExecuteDeleteAsync();
        }
    }

    /// <summary>Serves one canned text/event-stream response for the SSE parser test.</summary>
    private sealed class CannedSseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
