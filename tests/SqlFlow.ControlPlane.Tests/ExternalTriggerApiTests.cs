using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The external trigger surface over the API: an administrator's lifecycle of a trigger (and the refusals a configured one
/// answers with), both exchanges (a key, and a GitHub job's token through a stand-in validator), the fence that keeps a
/// trigger's credential to its own requests, and every check a request meets at the trust boundary.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExternalTriggerApiTests
{
    [SkippableFact]
    public async Task An_administrator_creates_changes_disables_rotates_and_removes_a_trigger()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        var operatorToken = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate"]);
        await using var estate = TriggerEstate.Seed(cs, "trg_admin", [new("land", "nightly")]);
        await estate.WithCatalogAsync(db => RepoSourceStore.UpsertAsync(db, estate.RepoName, estate.GitDir, estate.Branch, false, 3600, DateTime.UtcNow));
        var name = "admin-" + estate.RepoName[^8..];

        // A GitHub trigger: warned about what it trusts too broadly and about a schedule the repository lacks.
        using (var created = await TriggerEstate.Send(client, admin, HttpMethod.Post, "/api/v1/external-triggers",
            new ExternalTriggerWriteRequest(name, "github", estate.RepoName, "deliver after merge", false, ["nightly", "weekly"],
                new ExternalTriggerGitHubDto("acme/estate", null, "main", null, null))))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var written = (await created.Content.ReadFromJsonAsync<ExternalTriggerWritten>(TriggerEstate.Json))!;
            Assert.Null(written.Key);
            Assert.Equal("refs/heads/main", written.Trigger.GitHub!.Ref);
            Assert.Contains(written.Warnings, w => w.Contains("'weekly'", StringComparison.Ordinal));
            Assert.Contains(written.Warnings, w => w.Contains("every workflow", StringComparison.Ordinal));
            Assert.Contains(written.Warnings, w => w.Contains("not pinned", StringComparison.Ordinal));
        }

        using (var duplicate = await TriggerEstate.Send(client, admin, HttpMethod.Post, "/api/v1/external-triggers",
            new ExternalTriggerWriteRequest(name, "key", estate.RepoName, null, true, [], null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var unknownRepo = await TriggerEstate.Send(client, admin, HttpMethod.Post, "/api/v1/external-triggers",
            new ExternalTriggerWriteRequest(name + "-x", "key", "no_such_repo", null, true, [], null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownRepo.StatusCode);
        }

        using (var forbidden = await TriggerEstate.Send(client, operatorToken, HttpMethod.Get, "/api/v1/external-triggers"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // A key trigger: its key is answered once, and only its hash and prefix are kept.
        var keyTrigger = await estate.CreateTriggerAsync(client, admin, name + "-key", ["nightly"]);
        Assert.StartsWith("sqlft_", keyTrigger.Key, StringComparison.Ordinal);
        var stored = await estate.WithCatalogAsync(db => db.ExternalTriggers.AsNoTracking().SingleAsync(t => t.Id == keyTrigger.Trigger.Id));
        Assert.Equal(AccessTokenGenerator.HashSecret(keyTrigger.Key!), stored.KeyHash);
        Assert.Equal(keyTrigger.Key![..12], stored.KeyPrefix);
        var listed = await TriggerEstate.Get<IReadOnlyList<ExternalTriggerDto>>(client, admin, "/api/v1/external-triggers");
        Assert.Contains(listed, t => t.Id == keyTrigger.Trigger.Id && t.KeyPrefix == stored.KeyPrefix);

        // Changing it keeps its kind; changing the kind is refused.
        using (var kindChange = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/external-triggers/{keyTrigger.Trigger.Id:D}",
            new ExternalTriggerWriteRequest(name + "-key", "github", estate.RepoName, null, false, [], new ExternalTriggerGitHubDto("acme/estate", null, "main", null, null))))
        {
            Assert.Equal(HttpStatusCode.Conflict, kindChange.StatusCode);
        }

        using (var renamed = await TriggerEstate.Send(client, admin, HttpMethod.Put, $"/api/v1/external-triggers/{keyTrigger.Trigger.Id:D}",
            new ExternalTriggerWriteRequest(name + "-ops", "key", estate.RepoName, "ops", true, [], null)))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            var written = (await renamed.Content.ReadFromJsonAsync<ExternalTriggerWritten>(TriggerEstate.Json))!;
            Assert.Equal(name + "-ops", written.Trigger.Name);
            Assert.True(written.Trigger.AllSchedules);
        }

        // The key still works after a rename; a rotated key replaces it at once.
        await TriggerEstate.ExchangeKeyAsync(client, keyTrigger.Key!);
        using (var rotate = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/external-triggers/{keyTrigger.Trigger.Id:D}/key", new RotateTriggerKeyRequest(30)))
        {
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            var rotated = (await rotate.Content.ReadFromJsonAsync<ExternalTriggerWritten>(TriggerEstate.Json))!;
            Assert.NotEqual(keyTrigger.Key, rotated.Key);
            Assert.NotNull(rotated.Trigger.KeyExpiresUtc);
            await TriggerEstate.ExchangeKeyAsync(client, rotated.Key!);
        }

        using (var old = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/key", UriKind.Relative), new KeyTriggerExchangeRequest(keyTrigger.Key)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        using (var rotateGitHub = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/external-triggers/{listed.Single(t => t.Name == name).Id:D}/key", new RotateTriggerKeyRequest()))
        {
            Assert.Equal(HttpStatusCode.Conflict, rotateGitHub.StatusCode);
        }

        using (var delete = await TriggerEstate.Send(client, admin, HttpMethod.Delete, $"/api/v1/external-triggers/{keyTrigger.Trigger.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using var gone = await TriggerEstate.Send(client, admin, HttpMethod.Get, $"/api/v1/external-triggers/{keyTrigger.Trigger.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [SkippableFact]
    public async Task A_key_that_is_unknown_malformed_expired_or_disabled_is_refused_alike()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var estate = TriggerEstate.Seed(cs, "trg_keys", [new("land", "nightly")]);
        await estate.WithCatalogAsync(db => RepoSourceStore.UpsertAsync(db, estate.RepoName, estate.GitDir, estate.Branch, false, 3600, DateTime.UtcNow));
        var expired = await estate.CreateTriggerAsync(client, admin, "expired-" + estate.RepoName[^8..], ["nightly"]);
        await estate.WithCatalogAsync(db => db.ExternalTriggers.Where(t => t.Id == expired.Trigger.Id)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.KeyExpiresUtc, DateTime.UtcNow.AddMinutes(-1))));
        var disabled = await estate.CreateTriggerAsync(client, admin, "disabled-" + estate.RepoName[^8..], ["nightly"]);
        using (var off = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/external-triggers/{disabled.Trigger.Id:D}/disable"))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        }

        foreach (var key in new[] { expired.Key!, disabled.Key!, AccessTokenGenerator.MintTriggerKey().Secret, "sqlft_short", "sqlf_" + new string('a', 43), "  " })
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/key", UriKind.Relative), new KeyTriggerExchangeRequest(key));
            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest,
                $"'{key[..Math.Min(8, key.Length)]}' answered {(int)response.StatusCode}");
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
    }

    [SkippableFact]
    public async Task A_GitHub_job_is_matched_to_the_trigger_that_trusts_it_and_its_requests_record_where_they_came_from()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var validator = new StandInValidator();
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithServices(s =>
            {
                s.RemoveAll<IGitHubActionsTokenValidator>();
                s.AddSingleton<IGitHubActionsTokenValidator>(validator);
            });
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var estate = TriggerEstate.Seed(cs, "trg_gh", [new("land", "nightly")]);
        await estate.WithCatalogAsync(db => RepoSourceStore.UpsertAsync(db, estate.RepoName, estate.GitDir, estate.Branch, false, 3600, DateTime.UtcNow));
        var repository = "acme/" + estate.RepoName;
        var name = "gh-" + estate.RepoName[^8..];
        await estate.CreateTriggerAsync(client, admin, name, ["nightly"], kind: "github",
            gitHub: new ExternalTriggerGitHubDto(repository, "123456", "refs/heads/main", ".github/workflows/deliver.yml", null));

        validator.Next = new GitHubTokenValidation(Identity(repository, "refs/heads/main"), null);
        using var exchange = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest("token"));
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var session = (await exchange.Content.ReadFromJsonAsync<TriggerSessionResponse>(TriggerEstate.Json))!;
        Assert.Equal(name, session.Trigger.Name);
        Assert.Equal(["nightly"], session.Trigger.Schedules);

        var (status, request, body) = await TriggerEstate.RequestAsync(client, session.AccessToken, new CreateTriggerRequest(Sync: false, Schedules: ["nightly"]));
        Assert.True(status == HttpStatusCode.Accepted, body);
        Assert.Contains($"GitHub {repository} (id 123456)", request!.Origin, StringComparison.Ordinal);
        Assert.Equal($"https://github.com/{repository}/actions/runs/77/attempts/1", request.OriginUrl);
        Assert.Equal($"trigger:{name}", request.RequestedBy);

        var trigger = await estate.WithCatalogAsync(db => db.ExternalTriggers.AsNoTracking().SingleAsync(t => t.Name == name));
        Assert.Contains("run 77 attempt 1", trigger.LastUsedDetail, StringComparison.Ordinal);

        // A job on another ref is told which trigger asks for what; a bad token is a 401; an outage a 503.
        validator.Next = new GitHubTokenValidation(Identity(repository, "refs/heads/feature"), null);
        using (var wrongRef = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest("token")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongRef.StatusCode);
            Assert.Contains("trusts ref refs/heads/main, and the job runs on refs/heads/feature", await wrongRef.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        validator.Next = GitHubTokenValidation.Refused("The GitHub token has expired; ask GitHub for a new one.");
        using (var invalid = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest("token")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        }

        validator.Next = new GitHubTokenValidation(null, "GitHub's signing keys could not be read", Outage: true);
        using (var outage = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest("token")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        }

        using (var empty = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest(null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }
    }

    [SkippableFact]
    public async Task A_triggers_credential_reaches_its_requests_and_nothing_else()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<TokenIssuer>();
        var token = issuer.IssueTriggerCredential("trigger:fenced", Guid.NewGuid(), null, null, TimeSpan.FromMinutes(5), DateTime.UtcNow).Token;

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/v1/runs"),
            (HttpMethod.Get, "/api/v1/me"),
            (HttpMethod.Post, "/api/v1/me/tokens"),
            (HttpMethod.Post, "/api/v1/auth/renew"),
            (HttpMethod.Get, "/api/v1/schedules"),
            (HttpMethod.Post, $"/api/v1/schedules/{Guid.NewGuid():D}/run"),
            (HttpMethod.Post, "/api/v1/runs"),
            (HttpMethod.Get, "/api/v1/activities/stream?kind=trigger-request&subject=x"),
        })
        {
            using var response = await TriggerEstate.Send(client, token, method, path, method == HttpMethod.Post ? new { } : null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains(ExternalTriggerCredential.DeniedTitle, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // An admin endpoint refuses it by its policy first: a trigger's credential carries no scope at all.
        using (var adminOnly = await TriggerEstate.Send(client, token, HttpMethod.Get, "/api/v1/external-triggers"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, adminOnly.StatusCode);
        }

        // Its own surface answers, as a trigger: a credential of a trigger that no longer exists makes no request.
        using var create = await TriggerEstate.Send(client, token, HttpMethod.Post, "/api/v1/trigger-requests", new CreateTriggerRequest());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("disabled or removed", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_request_is_checked_at_the_trust_boundary()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("ControlPlane:ManagedSync:Enabled", "false");
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var estate = TriggerEstate.Seed(cs, "trg_checks", [new("land", "nightly")]);
        await estate.WithCatalogAsync(db => RepoSourceStore.UpsertAsync(db, estate.RepoName, estate.GitDir, estate.Branch, true, 3600, DateTime.UtcNow));
        var trigger = await estate.CreateTriggerAsync(client, admin, "checks-" + estate.RepoName[^8..], ["nightly"]);
        var token = await TriggerEstate.ExchangeKeyAsync(client, trigger.Key!);

        async Task Expect(HttpStatusCode status, string? token2, CreateTriggerRequest body, string text)
        {
            var (actual, _, answer) = await TriggerEstate.RequestAsync(client, token2 ?? token, body);
            Assert.True(actual == status, $"{text}: {(int)actual} {answer}");
            Assert.Contains(text, answer, StringComparison.Ordinal);
        }

        await Expect(HttpStatusCode.BadRequest, null, new CreateTriggerRequest(Commit: "not-a-sha"), "7 to 64 hexadecimal");
        await Expect(HttpStatusCode.BadRequest, null, new CreateTriggerRequest(Commit: "abcdef1234", Sync: false), "leave sync on");
        await Expect(HttpStatusCode.Forbidden, null, new CreateTriggerRequest(Schedules: ["nightly", "hourly"]), "may not fire 'hourly'. It may fire: nightly");
        await Expect(HttpStatusCode.Forbidden, null, new CreateTriggerRequest(Repo: "another_repo"), "syncs '" + estate.RepoName + "', not 'another_repo'");
        await Expect(HttpStatusCode.BadRequest, null, new CreateTriggerRequest(Batch: ["small"]), "name the schedules to fire");
        await Expect(HttpStatusCode.BadRequest, null, new CreateTriggerRequest(Schedules: [.. Enumerable.Range(0, 51).Select(i => "s" + i)]), "at most 50 schedules");
        await Expect(HttpStatusCode.BadRequest, null, new CreateTriggerRequest(RequestKey: new string('k', 201)), "requestKey is at most 200");
        await Expect(HttpStatusCode.BadRequest, admin, new CreateTriggerRequest(), "Name the repository");
        await Expect(HttpStatusCode.NotFound, admin, new CreateTriggerRequest(Repo: "no_such_repo_" + estate.RepoName), "No repo source named");

        await estate.WithCatalogAsync(db => db.RepoSources.Where(s => s.Id == estate.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false)));
        await Expect(HttpStatusCode.Conflict, null, new CreateTriggerRequest(), "is disabled");
        var (allowed, request, _) = await TriggerEstate.RequestAsync(client, token, new CreateTriggerRequest(Sync: false));
        Assert.Equal(HttpStatusCode.Accepted, allowed);
        Assert.False(request!.Sync);
    }

    [SkippableFact]
    public async Task External_triggers_turned_off_are_refused_everywhere_they_enter()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("ControlPlane:ExternalTriggers:Enabled", "false");
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        using var key = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/key", UriKind.Relative), new KeyTriggerExchangeRequest("sqlft_" + new string('a', 43)));
        using var github = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest("t"));
        var (request, _, _) = await TriggerEstate.RequestAsync(client, admin, new CreateTriggerRequest(Repo: "any"));
        var setup = await TriggerEstate.Get<ExternalTriggerSetupDto>(client, admin, "/api/v1/external-triggers/setup");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, key.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, github.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, request);
        Assert.False(setup.Enabled);
        Assert.Equal("sqlflow", setup.GitHubAudience);
    }

    [SkippableFact]
    public async Task A_configured_trigger_is_written_at_start_refuses_the_API_and_is_disabled_once_the_configuration_drops_it()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "configured-" + suffix;
        var repo = "trg_conf_" + suffix;
        var keyVariable = "SQLFLOW_TEST_TRIGGER_KEY_" + suffix.ToUpperInvariant();
        var key = AccessTokenGenerator.MintTriggerKey().Secret;
        Environment.SetEnvironmentVariable(keyVariable, key);
        try
        {
            await using (var factory = new ControlPlaneAppFactory().WithCatalog(cs)
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting($"ControlPlane:Bootstrap:ExternalTriggers:{name}:RepoSource", repo)
                .WithSetting($"ControlPlane:Bootstrap:ExternalTriggers:{name}:Schedules:0", "nightly")
                .WithSetting($"ControlPlane:Bootstrap:ExternalTriggers:{name}:KeyReference", $"${{env:{keyVariable}}}"))
            {
                using var client = factory.CreateClient();
                var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
                var trigger = await WaitForTriggerAsync(cs, name, t => t.ConfiguredBy is not null);
                Assert.Equal($"ControlPlane:Bootstrap:ExternalTriggers:{name}", trigger.ConfiguredBy);
                Assert.Equal(AccessTokenGenerator.HashSecret(key), trigger.KeyHash);
                await TriggerEstate.ExchangeKeyAsync(client, key);

                using var change = await TriggerEstate.Send(client, admin, HttpMethod.Post, $"/api/v1/external-triggers/{trigger.Id:D}/disable");
                Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
                Assert.Contains("ControlPlane:Bootstrap:ExternalTriggers:", await change.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                using var delete = await TriggerEstate.Send(client, admin, HttpMethod.Delete, $"/api/v1/external-triggers/{trigger.Id:D}");
                Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            }

            await using (var factory = new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false"))
            {
                using var client = factory.CreateClient();
                var released = await WaitForTriggerAsync(cs, name, t => t.ConfiguredBy is null);
                Assert.False(released.Enabled);
                using var exchange = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/key", UriKind.Relative), new KeyTriggerExchangeRequest(key));
                Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, null);
            await using var db = CatalogDatabase.Create(cs);
            await db.ExternalTriggers.Where(t => t.Name == name).ExecuteDeleteAsync();
        }
    }

    private static async Task<CatalogExternalTrigger> WaitForTriggerAsync(string cs, string name, Func<CatalogExternalTrigger, bool> until)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            await using var db = CatalogDatabase.Create(cs);
            if (await db.ExternalTriggers.AsNoTracking().FirstOrDefaultAsync(t => t.Name == name) is { } trigger && until(trigger))
            {
                return trigger;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"External trigger '{name}' did not reach the expected state.");
    }

    private static GitHubActionsIdentity Identity(string repository, string gitRef)
        => new(repository, "123456", "acme", "42", gitRef, "0123456789abcdef0123456789abcdef01234567",
            $"{repository}/.github/workflows/deliver.yml@{gitRef}", null, null, "push", "77", "1", "octocat", null);

    /// <summary>A validator that answers what the test sets, so the exchange is exercised without GitHub.</summary>
    private sealed class StandInValidator : IGitHubActionsTokenValidator
    {
        public GitHubTokenValidation Next { get; set; } = GitHubTokenValidation.Refused("not set");

        public Task<GitHubTokenValidation> ValidateAsync(string token, CancellationToken ct) => Task.FromResult(Next);
    }
}
