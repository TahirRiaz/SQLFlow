using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The start-up pass: one ordered pass of named steps, reported step by step by readiness and <c>GET /api/v1/bootstrap</c>;
/// the repo sources the configuration names, written at start, locked against edits through the API and released when the
/// configuration stops naming them; and the node credential it names, made the one active node token and rotated with its
/// secret, so a fresh deployment's workers connect with no token minted by hand.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BootstrapPassTests
{
    // ---- What the configuration may name ---------------------------------------------------------------------------

    [Fact]
    public void TheConfiguredRepositories_AreTheDemoRepoAndTheNamedOnes_EachUnderItsOwnKey()
    {
        var options = new BootstrapOptions
        {
            DemoRepo = new DemoRepoOptions { Name = "samples", RemoteUrl = "https://git.example.com/samples.git" },
            Repositories =
            {
                ["estate"] = new ConfiguredRepositoryOptions
                {
                    RemoteUrl = "https://github.com/example/estate.git",
                    Branch = "release",
                    CredentialReference = "${keyvault:estate-vault/github-token}",
                    ExcludedFlowPaths = ["flows/old.flow.yaml"],
                },
                ["local"] = new ConfiguredRepositoryOptions { LocalPath = "C:/flows", SyncIntervalSeconds = 60 },
            },
        };

        var configured = options.ConfiguredRepositories();

        Assert.Equal(["samples", "estate", "local"], configured.Select(r => r.Name));
        Assert.Equal(
            [BootstrapOptions.DemoRepoKey, "ControlPlane:Bootstrap:Repositories:estate", "ControlPlane:Bootstrap:Repositories:local"],
            configured.Select(r => r.Setting));
        var estate = configured[1];
        Assert.Equal(("https://github.com/example/estate.git", "release", "${keyvault:estate-vault/github-token}"), (estate.RemoteUrl, estate.Branch, estate.CredentialReference));
        Assert.Equal(["flows/old.flow.yaml"], estate.ExcludedFlowPaths);
        Assert.Equal(("C:/flows", 60), (configured[2].LocalPath, configured[2].SyncIntervalSeconds));
    }

    public static TheoryData<ConfiguredRepositoryOptions?, string> RefusedRepositories => new()
    {
        { null, "sets nothing" },
        { new ConfiguredRepositoryOptions(), "needs exactly one of RemoteUrl" },
        { new ConfiguredRepositoryOptions { RemoteUrl = "https://x/y.git", LocalPath = "C:/flows" }, "needs exactly one of RemoteUrl" },
        { new ConfiguredRepositoryOptions { RemoteUrl = "estate.git" }, "RemoteUrl is not an absolute URL" },
        { new ConfiguredRepositoryOptions { RemoteUrl = "https://x/y.git", SyncIntervalSeconds = 0 }, "SyncIntervalSeconds must be positive" },
        { new ConfiguredRepositoryOptions { RemoteUrl = "https://x/y.git", CredentialReference = "ghp_secret" }, "never the token itself" },
        { new ConfiguredRepositoryOptions { LocalPath = "C:/flows", CredentialReference = "${env:GIT_TOKEN}" }, "applies to a git remote" },
    };

    [Theory]
    [MemberData(nameof(RefusedRepositories))]
    public void ARepositoryTheConfigurationMayNotHold_IsRefused_NamingItsSetting(ConfiguredRepositoryOptions? repository, string because)
    {
        var options = new BootstrapOptions { Repositories = { ["estate"] = repository } };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.StartsWith("ControlPlane:Bootstrap:Repositories:estate", ex.Message, StringComparison.Ordinal);
        Assert.Contains(because, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepositoryNamedTwice_IsRefused()
    {
        var options = new BootstrapOptions
        {
            DemoRepo = new DemoRepoOptions { Name = "Estate", RemoteUrl = "https://x/a.git" },
            Repositories = { ["estate"] = new ConfiguredRepositoryOptions { RemoteUrl = "https://x/b.git" } },
        };

        Assert.Contains("another setting names too", Assert.Throws<InvalidOperationException>(options.Validate).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlf_written-out-token", "admin", "never the token itself")]
    [InlineData("${env:NODE_TOKEN}", null, "needs an owner")]
    public void ANodeTokenTheConfigurationMayNotHold_IsRefused(string reference, string? admin, string because)
    {
        var options = new BootstrapOptions
        {
            NodeTokenReference = reference,
            AdminUsername = admin,
            AdminPasswordReference = admin is null ? null : "${env:ADMIN_PASSWORD}",
        };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.StartsWith(BootstrapOptions.NodeTokenKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains(because, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("written-out-token", ex.Message, StringComparison.Ordinal);
    }

    // ---- How the pass reports itself -------------------------------------------------------------------------------

    [Fact]
    public async Task Readiness_NamesTheStepThePassWaitsFor_ThenTheOneThatFailed_ThenSaysItIsDone()
    {
        var progress = new BootstrapProgress();
        var readiness = new BootstrapReadiness(progress);
        var now = DateTime.UtcNow;

        Assert.Equal("The start-up pass has not begun.", (await readiness.CheckHealthAsync(null!)).Description);

        progress.Plan(["catalog", "repositories"]);
        Assert.Equal("Waiting for start-up step 'catalog'.", (await readiness.CheckHealthAsync(null!)).Description);

        progress.Started("catalog", now);
        progress.Succeeded("catalog", "current", now);
        progress.Started("repositories", now);
        progress.Failed("repositories", "the catalog did not answer", now);
        var failed = await readiness.CheckHealthAsync(null!);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, failed.Status);
        Assert.Equal("Start-up step 'repositories' failed and is retried.", failed.Description);
        Assert.False(progress.Completed);

        progress.Started("repositories", now);
        progress.Succeeded("repositories", "registered estate", now);
        var done = await readiness.CheckHealthAsync(null!);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, done.Status);
        Assert.True(progress.Completed);
        var repositories = progress.Steps[1];
        Assert.Equal((BootstrapStepStates.Done, "registered estate", (string?)null, 2), (repositories.State, repositories.Summary, repositories.Error, repositories.Attempts));
    }

    // ---- Configured repo sources in the catalog -------------------------------------------------------------------

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ConfiguredSources_AreRegistered_BroughtInLine_AndReleased_NeverDeleted()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string first = "cfg-a-" + suffix, second = "cfg-b-" + suffix;
        var now = DateTime.UtcNow;
        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var both = new[] { Configured(first, "https://git.invalid/a.git"), Configured(second, "https://git.invalid/b.git") };

            var applied = await RepoSourceStore.ApplyConfiguredAsync(db, both, now);
            Assert.Equal(new[] { first, second }, applied.Registered.Where(n => n.EndsWith(suffix, StringComparison.Ordinal)));
            Assert.DoesNotContain((await RepoSourceStore.ApplyConfiguredAsync(db, both, now)).Registered, n => n.EndsWith(suffix, StringComparison.Ordinal));

            // A changed branch is brought in line; the source the configuration stops naming is kept as an ordinary one.
            var changed = await RepoSourceStore.ApplyConfiguredAsync(db, [Configured(first, "https://git.invalid/a.git") with { Branch = "release" }], now);
            Assert.Contains(first, changed.Updated);
            Assert.Contains(second, changed.Released);

            var rows = await db.RepoSources.AsNoTracking().Where(s => s.Name == first || s.Name == second).ToDictionaryAsync(s => s.Name);
            Assert.Equal(("release", BootstrapOptions.RepositoryKey(first)), (rows[first].Branch, rows[first].ConfiguredBy));
            Assert.Null(rows[second].ConfiguredBy);
            Assert.Equal(BootstrapOptions.RepositoryKey(first), await RepoSourceStore.ConfiguredByNameAsync(db, first));
            Assert.Null(await RepoSourceStore.ConfiguredByNameAsync(db, second));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Name == first || s.Name == second).ExecuteDeleteAsync();
        }
    }

    // ---- The configured node credential ----------------------------------------------------------------------------

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TheConfiguredNodeToken_IsIssued_KeptAsIs_RotatedWithItsSecret_AndRevokedWithItsSetting()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var setting = "Test:NodeToken:" + Guid.NewGuid().ToString("N");
        var owner = await NewAdminAsync(cs);
        var now = DateTime.UtcNow;
        var (first, second) = (AccessTokenGenerator.Mint(), AccessTokenGenerator.Mint());
        try
        {
            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal("issued", await AccessTokenStore.EnsureConfiguredAsync(db, setting, owner, "node", first.Hash, first.DisplayPrefix, "node", now));
            Assert.Equal("unchanged", await AccessTokenStore.EnsureConfiguredAsync(db, setting, owner, "node", first.Hash, first.DisplayPrefix, "node", now));
            var issued = await AccessTokenStore.FindActiveByHashAsync(db, first.Hash, now);
            Assert.NotNull(issued);
            Assert.Equal(["node"], issued.TokenScopes);

            // Nobody revokes it through the API: the next start would only issue it again.
            Assert.Equal(setting, await AccessTokenStore.ConfiguredByAsync(db, owner, issued.TokenId));
            Assert.False(await AccessTokenStore.RevokeAsync(db, owner, issued.TokenId, now));

            // A rotated secret replaces it.
            Assert.Equal("issued; 1 earlier one(s) revoked", await AccessTokenStore.EnsureConfiguredAsync(db, setting, owner, "node", second.Hash, second.DisplayPrefix, "node", now));
            Assert.Null(await AccessTokenStore.FindActiveByHashAsync(db, first.Hash, now));
            Assert.NotNull(await AccessTokenStore.FindActiveByHashAsync(db, second.Hash, now));

            // The setting gone, its token goes too; named again, it is taken back into service.
            Assert.Equal(1, await AccessTokenStore.RevokeConfiguredAsync(db, setting, now));
            Assert.Null(await AccessTokenStore.FindActiveByHashAsync(db, second.Hash, now));
            Assert.Equal("taken back into service", await AccessTokenStore.EnsureConfiguredAsync(db, setting, owner, "node", second.Hash, second.DisplayPrefix, "node", now));
            Assert.NotNull(await AccessTokenStore.FindActiveByHashAsync(db, second.Hash, now));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.AccessTokens.Where(t => t.UserId == owner).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner).ExecuteDeleteAsync();
        }
    }

    // ---- The pass in a running control plane -----------------------------------------------------------------------

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AControlPlane_StartsWithItsConfiguredSourceAndNodeCredential_AndLocksThem()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = "bootstrap-pass-" + suffix;
        const string password = "bootstrap-pass-password-1";
        var source = "configured-" + suffix;
        var tokenVariable = "SQLFLOW_TEST_NODE_TOKEN_" + suffix.ToUpperInvariant();
        var token = AccessTokenGenerator.Mint();
        var flows = Directory.CreateTempSubdirectory("sqlflow-bootstrap-pass-").FullName;
        Environment.SetEnvironmentVariable(tokenVariable, token.Secret);
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("ControlPlane:Bootstrap:AdminUsername", admin)
                .WithSetting("ControlPlane:Bootstrap:AdminPasswordReference", password)
                .WithSetting($"ControlPlane:Bootstrap:Repositories:{source}:LocalPath", flows)
                .WithSetting("ControlPlane:Bootstrap:NodeTokenReference", "${env:" + tokenVariable + "}");
            using var client = factory.CreateClient();
            var adminToken = await IssueTokenAsync(client, ["read", "operate", "admin"]);

            var bootstrap = await WaitForBootstrapAsync(client, adminToken);
            using (var startup = await client.GetAsync(new Uri("/health/startup", UriKind.Relative)))
            {
                Assert.Equal(HttpStatusCode.OK, startup.StatusCode);
            }

            Assert.Equal(
                ["catalog", "module-databases", "roles", "admin", "repositories", "node-credential", "worker-pool"],
                bootstrap.Steps.Select(s => s.Name));
            Assert.All(bootstrap.Steps, step => Assert.Equal(BootstrapStepStates.Done, step.State));
            Assert.Contains($"registered {source}", bootstrap.Steps.Single(s => s.Name == "repositories").Summary, StringComparison.Ordinal);

            // The configured source is locked: re-registering or removing it is refused, naming the setting.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var row = await db.RepoSources.AsNoTracking().SingleAsync(s => s.Name == source);
                Assert.Equal(BootstrapOptions.RepositoryKey(source), row.ConfiguredBy);

                using var reregister = await SendAsync(client, adminToken, HttpMethod.Post, "/api/v1/repos/sources",
                    new RegisterRepoSourceRequest(source, "https://git.invalid/other.git", "main", 60, true));
                Assert.Equal(HttpStatusCode.Conflict, reregister.StatusCode);
                Assert.Contains(BootstrapOptions.RepositoryKey(source), await reregister.Content.ReadAsStringAsync(), StringComparison.Ordinal);

                using var remove = await SendAsync(client, adminToken, HttpMethod.Delete, $"/api/v1/repos/sources/{row.Id}", null);
                Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);

                // The configured node credential authenticates as the admin's node-scope token.
                var auth = await AccessTokenStore.FindActiveByHashAsync(db, token.Hash, DateTime.UtcNow);
                Assert.NotNull(auth);
                Assert.Equal(admin, auth.Username);
                Assert.Equal(["node"], auth.TokenScopes);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, null);
            await using var db = CatalogDatabase.Create(cs);
            var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Name == source);
            if (repo is not null)
            {
                await RepoStore.DeleteAsync(db, repo.Id);
            }

            await db.RepoSources.Where(s => s.Name == source).ExecuteDeleteAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == admin);
            if (user is not null)
            {
                await db.AccessTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync();
                await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
            }

            Directory.Delete(flows, recursive: true);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AStepThatKeepsFailing_LeavesTheControlPlaneServing_AndSaysWhichStepAndWhy()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = "bootstrap-fail-" + suffix;
        var tokenVariable = "SQLFLOW_TEST_BAD_NODE_TOKEN_" + suffix.ToUpperInvariant();
        Environment.SetEnvironmentVariable(tokenVariable, "not-a-node-token");
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("ControlPlane:Bootstrap:AdminUsername", admin)
                .WithSetting("ControlPlane:Bootstrap:AdminPasswordReference", "bootstrap-fail-password-1")
                .WithSetting("ControlPlane:Bootstrap:NodeTokenReference", "${env:" + tokenVariable + "}");
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            BootstrapStepDto? failed = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (failed is null)
            {
                using var response = await SendAsync(client, token, HttpMethod.Get, "/api/v1/bootstrap", null);
                var bootstrap = (await response.Content.ReadFromJsonAsync<BootstrapDto>())!;
                failed = bootstrap.Steps.FirstOrDefault(s => s.Name == "node-credential" && s.State == BootstrapStepStates.Failed);
                Assert.True(failed is not null || DateTime.UtcNow < deadline, "The node-credential step never failed.");
                if (failed is null)
                {
                    await Task.Delay(250);
                }
            }

            Assert.False(failed.Error is null || failed.Error.Contains("not-a-node-token", StringComparison.Ordinal));
            Assert.Contains("is not a node token of SQLFlow's form", failed.Error, StringComparison.Ordinal);

            using var startup = await client.GetAsync(new Uri("/health/startup", UriKind.Relative));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, startup.StatusCode);
            using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, null);
            await using var db = CatalogDatabase.Create(cs);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == admin);
            if (user is not null)
            {
                await db.AccessTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync();
                await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
            }
        }
    }

    private static ConfiguredRepository Configured(string name, string remote)
        => new(name, remote, null, "main", 300, null, null, [], BootstrapOptions.RepositoryKey(name));

    private static async Task<Guid> NewAdminAsync(string cs)
    {
        await using var db = CatalogDatabase.Create(cs);
        await UserStore.EnsureRoleAsync(db, RoleNames.Admin, "read operate admin author node", "Full control.", DateTime.UtcNow);
        var (status, id) = await UserStore.CreateLocalAsync(
            db, "node-owner-" + Guid.NewGuid().ToString("N")[..8], "not-a-real-hash", RoleNames.Admin, null, null, DateTime.UtcNow);
        Assert.Equal(UserCreateStatus.Created, status);
        return id;
    }

    private static async Task<BootstrapDto> WaitForBootstrapAsync(HttpClient client, string token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            using var response = await SendAsync(client, token, HttpMethod.Get, "/api/v1/bootstrap", null);
            response.EnsureSuccessStatusCode();
            var bootstrap = (await response.Content.ReadFromJsonAsync<BootstrapDto>())!;
            var refused = bootstrap.Steps.FirstOrDefault(s => s.State == BootstrapStepStates.Refused);
            Assert.True(refused is null, $"Start-up step '{refused?.Name}' was refused: {refused?.Error}");
            if (bootstrap.Completed)
            {
                return bootstrap;
            }

            Assert.True(DateTime.UtcNow < deadline, "The start-up pass did not complete: " + string.Join("; ", bootstrap.Steps.Select(s => $"{s.Name} {s.State} {s.Error}")));
            await Task.Delay(250);
        }
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
