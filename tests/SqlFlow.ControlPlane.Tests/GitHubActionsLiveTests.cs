using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A token a real GitHub Actions job minted, checked as the control plane checks one: against GitHub's published signing
/// keys and the configured audience, matched to the trigger that trusts the job, and exchanged at the real endpoint for a
/// credential that makes a request naming the run. The stand-in validator of the other suites proves the rules; this
/// proves GitHub's tokens are what those rules expect. It needs a private repository holding the probe workflow and a
/// token that may run it (<see cref="LiveGitHubOidc"/>): with neither set the suite is skipped, with one of them it
/// fails, naming the other. Each run of the suite starts one workflow run, which it deletes with its artifact.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "ExternalTriggers")]
public sealed class GitHubActionsLiveTests(LiveGitHubOidcToken probe) : IClassFixture<LiveGitHubOidcToken>
{
    /// <summary>The minted token, or a skip naming the variables that configure the probe.</summary>
    private LiveGitHubOidcToken Probe
    {
        get
        {
            Skip.If(!probe.Configured, LiveGitHubOidc.NotConfigured);
            return probe;
        }
    }

    [SkippableFact]
    public async Task A_token_GitHub_minted_for_a_job_is_valid_and_names_the_job()
    {
        var job = Probe;

        var validation = await Validator(job.Audience).ValidateAsync(job.Token, CancellationToken.None);

        Assert.True(validation.Identity is not null, validation.Problem);
        var identity = validation.Identity!;
        Assert.Equal(job.Repository, identity.Repository);
        Assert.Equal(job.RepositoryId, identity.RepositoryId);
        Assert.Equal("refs/heads/" + job.DefaultBranch, identity.Ref);
        Assert.Equal(LiveGitHubOidc.WorkflowPath, identity.WorkflowPath);
        Assert.Equal("workflow_dispatch", identity.EventName);
        Assert.Equal(job.RunId, identity.RunId);
        Assert.Equal("1", identity.RunAttempt);
        Assert.Matches("^[0-9a-f]{40}$", identity.Sha);
        Assert.False(string.IsNullOrWhiteSpace(identity.Actor));
        Assert.Null(identity.Environment);
        Assert.Equal($"https://github.com/{job.Repository}/actions/runs/{job.RunId}/attempts/1", identity.RunUrl("https://github.com"));
    }

    [SkippableFact]
    public async Task The_same_token_is_refused_by_a_control_plane_that_checks_another_audience()
    {
        var job = Probe;

        var validation = await Validator("sqlflow-another-deployment").ValidateAsync(job.Token, CancellationToken.None);

        Assert.Null(validation.Identity);
        Assert.False(validation.Outage);
        Assert.Contains($"is for audience '{job.Audience}'", validation.Problem, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_token_altered_after_GitHub_signed_it_is_refused()
    {
        var job = Probe;
        var parts = job.Token.Split('.');
        var signature = parts[2];
        var altered = $"{parts[0]}.{parts[1]}.{(signature[0] == 'A' ? 'B' : 'A')}{signature[1..]}";

        var validation = await Validator(job.Audience).ValidateAsync(altered, CancellationToken.None);

        Assert.Null(validation.Identity);
        Assert.False(validation.Outage);
        Assert.False(string.IsNullOrWhiteSpace(validation.Problem));
    }

    [SkippableFact]
    public async Task The_trigger_that_trusts_the_job_is_chosen_and_the_others_are_told_what_differs()
    {
        var job = Probe;
        var identity = (await Validator(job.Audience).ValidateAsync(job.Token, CancellationToken.None)).Identity;
        Assert.NotNull(identity);
        var branch = "refs/heads/" + job.DefaultBranch;
        var trusted = Trigger("probe", job.Repository, job.RepositoryId, branch, LiveGitHubOidc.WorkflowPath, null);
        var release = Trigger("release", job.Repository, job.RepositoryId, "refs/heads/release/*", LiveGitHubOidc.WorkflowPath, null);
        var squatted = Trigger("squatted", job.Repository, "1", branch, null, null);
        var production = Trigger("production", job.Repository, null, branch, null, "production");

        var choice = GitHubTriggerMatch.Choose([trusted, release, squatted, production], identity, requestedName: null);

        Assert.Same(trusted, choice.Trigger);
        Assert.Contains($"it trusts ref refs/heads/release/*, and the job runs on {branch}", GitHubTriggerMatch.Mismatch(release, identity), StringComparison.Ordinal);
        Assert.Contains($"pinned to repository id 1, and the job's repository has id {job.RepositoryId}", GitHubTriggerMatch.Mismatch(squatted, identity), StringComparison.Ordinal);
        Assert.Contains("the job runs in none (set 'environment: production' on the job)", GitHubTriggerMatch.Mismatch(production, identity), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_control_plane_exchanges_the_jobs_token_for_a_credential_that_makes_a_request_naming_the_run()
    {
        var job = Probe;
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("ControlPlane:ExternalTriggers:GitHub:Audience", job.Audience);
        using var client = factory.CreateClient();
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var estate = TriggerEstate.Seed(cs, "trg_live", [new("land", "nightly")]);
        await estate.WithCatalogAsync(db => RepoSourceStore.UpsertAsync(db, estate.RepoName, estate.GitDir, estate.Branch, false, 3600, DateTime.UtcNow));
        var name = "live-" + estate.RepoName[^8..];
        await estate.CreateTriggerAsync(client, admin, name, ["nightly"], kind: "github",
            gitHub: new ExternalTriggerGitHubDto(job.Repository, job.RepositoryId, "refs/heads/" + job.DefaultBranch, LiveGitHubOidc.WorkflowPath, null));

        using var exchange = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/github", UriKind.Relative), new GitHubTriggerExchangeRequest(job.Token));
        Assert.True(exchange.StatusCode == HttpStatusCode.OK, await exchange.Content.ReadAsStringAsync());
        var session = (await exchange.Content.ReadFromJsonAsync<TriggerSessionResponse>(TriggerEstate.Json))!;
        Assert.Equal(name, session.Trigger.Name);

        var (status, request, body) = await TriggerEstate.RequestAsync(client, session.AccessToken, new CreateTriggerRequest(Sync: false, Schedules: ["nightly"]));

        Assert.True(status == HttpStatusCode.Accepted, body);
        Assert.Contains($"GitHub {job.Repository} (id {job.RepositoryId})", request!.Origin, StringComparison.Ordinal);
        Assert.Equal($"https://github.com/{job.Repository}/actions/runs/{job.RunId}/attempts/1", request.OriginUrl);
        Assert.Equal($"trigger:{name}", request.RequestedBy);
        using var fenced = await TriggerEstate.Send(client, session.AccessToken, HttpMethod.Get, "/api/v1/runs");
        Assert.Equal(HttpStatusCode.Forbidden, fenced.StatusCode);
    }

    /// <summary>The validator as the control plane composes it, reading GitHub's real metadata, for <paramref name="audience"/>.</summary>
    private static GitHubActionsTokenValidator Validator(string audience)
        => new(
            Options.Create(new ControlPlaneOptions { ExternalTriggers = new ExternalTriggerOptions { GitHub = new GitHubTriggerOptions { Audience = audience } } }),
            NullLogger<GitHubActionsTokenValidator>.Instance);

    private static CatalogExternalTrigger Trigger(string name, string repository, string? repositoryId, string gitRef, string? workflow, string? environment)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = ExternalTriggerKinds.GitHub,
            RepoName = "estate",
            Enabled = true,
            GitHubRepository = repository,
            GitHubRepositoryId = repositoryId,
            GitHubRef = gitRef,
            GitHubWorkflow = workflow,
            GitHubEnvironment = environment,
        };
}

/// <summary>
/// The probe the live suite asks GitHub through: a private repository holding <see cref="WorkflowPath"/> (a copy of
/// <see cref="FixturePath"/>) on its default branch, and a GitHub token that may run its workflows and read and delete
/// their artifacts (a fine-grained token with Actions: Read and write on that repository, or <c>gh auth token</c>).
/// </summary>
internal sealed class LiveGitHubOidc
{
    public const string RepositoryVariable = "SQLFLOW_TEST_GITHUB_OIDC_REPOSITORY";
    public const string TokenVariable = "SQLFLOW_TEST_GITHUB_OIDC_TOKEN";
    public const string WorkflowFile = "sqlflow-oidc-probe.yml";
    public const string WorkflowPath = ".github/workflows/" + WorkflowFile;
    public const string FixturePath = "sqlflow/tests/SqlFlow.ControlPlane.Tests/GitHubOidcProbe/" + WorkflowFile;

    /// <summary>Why the live tests are skipped where no probe is configured, and how to configure one.</summary>
    public const string NotConfigured =
        $"No GitHub OIDC probe is configured, so the GitHub Actions live tests are skipped: set {RepositoryVariable} (owner/name "
        + $"of a private repository whose default branch holds {WorkflowPath}, a copy of {FixturePath}) and {TokenVariable} (a "
        + "token with Actions: Read and write on it; tools/test.sh takes gh auth token when it is not set), in the environment "
        + "or in .sqlflow/env, to run them.";

    private LiveGitHubOidc(GitHubRepositoryName repository, string token)
    {
        Repository = repository;
        Token = token;
    }

    public GitHubRepositoryName Repository { get; }

    public string Token { get; }

    /// <summary>
    /// The probe the environment names; null when it names none of it, which skips the live tests. A probe named in part
    /// is a mistake rather than a choice, so it is refused, naming the setting it lacks.
    /// </summary>
    public static LiveGitHubOidc? FromEnvironment()
    {
        var repositoryText = Value(RepositoryVariable);
        var token = Value(TokenVariable);
        if (repositoryText is null && token is null)
        {
            return null;
        }

        var repository = GitHubRepositoryName.TryParse(repositoryText);
        var missing = new List<string>();
        if (repository is null)
        {
            missing.Add(repositoryText is null
                ? $"{RepositoryVariable} (owner/name of the private repository holding {WorkflowPath})"
                : $"{RepositoryVariable} as owner/name ('{repositoryText}' is not)");
        }

        if (token is null)
        {
            missing.Add($"{TokenVariable} (a token with Actions: Read and write on the repository)");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The GitHub OIDC probe is configured only in part: set " + string.Join(", and ", missing)
                + ", or remove its settings to skip the GitHub Actions live tests.");
        }

        return new LiveGitHubOidc(repository!, token!);
    }

    /// <summary>A client of GitHub's REST API under the probe's token. It follows no redirect, so the token never travels
    /// to the storage an artifact download is sent on to.</summary>
    public HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sqlflow-live-test", "1.0"));
        return client;
    }

    private static string? Value(string name)
        => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// One token a real GitHub Actions job minted for the live tests, shared by them: the probe workflow is started with an
/// audience of this run's own (which nothing else accepts), the run is found by the name it is given, its token is read
/// from the artifact it keeps, and the artifact is deleted at once. Disposing waits for the run to finish and deletes it,
/// with its logs. A token lives five minutes, which the tests take well within.
/// </summary>
public sealed class LiveGitHubOidcToken : IAsyncLifetime
{
    private const string ArtifactName = "oidc-token";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    private HttpClient? _api;
    private long _runId;

    public bool Configured { get; private set; }

    public string Repository { get; private set; } = string.Empty;

    public string RepositoryId { get; private set; } = string.Empty;

    public string DefaultBranch { get; private set; } = string.Empty;

    public string Audience { get; private set; } = string.Empty;

    public string RunId { get; private set; } = string.Empty;

    public string Token { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var settings = LiveGitHubOidc.FromEnvironment();
        if (settings is null)
        {
            return;
        }

        _api = settings.CreateClient();
        var repository = await GetAsync($"repos/{settings.Repository}");
        Repository = repository.GetProperty("full_name").GetString()!;
        RepositoryId = repository.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
        DefaultBranch = repository.GetProperty("default_branch").GetString()!;

        var nonce = "live-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        Audience = "sqlflow-" + nonce;
        using (var dispatch = await _api.PostAsJsonAsync(
            new Uri($"repos/{Repository}/actions/workflows/{LiveGitHubOidc.WorkflowFile}/dispatches", UriKind.Relative),
            new { @ref = DefaultBranch, inputs = new { audience = Audience, nonce } }))
        {
            if (dispatch.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"{Repository} has no workflow {LiveGitHubOidc.WorkflowFile} on {DefaultBranch}: copy {LiveGitHubOidc.FixturePath} to "
                    + $"{LiveGitHubOidc.WorkflowPath} there. GitHub answered: {await dispatch.Content.ReadAsStringAsync()}");
            }

            if (!dispatch.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GitHub refused to start {LiveGitHubOidc.WorkflowFile} in {Repository} ({(int)dispatch.StatusCode}); the token needs "
                    + $"Actions: Read and write on it. GitHub answered: {await dispatch.Content.ReadAsStringAsync()}");
            }
        }

        _runId = await FindRunAsync(nonce);
        RunId = _runId.ToString(CultureInfo.InvariantCulture);
        Token = await TakeTokenAsync();
        Configured = true;
    }

    public async Task DisposeAsync()
    {
        if (_api is null)
        {
            return;
        }

        using (_api)
        {
            if (_runId == 0)
            {
                return;
            }

            // A run is deleted only once it has finished; the token's artifact is gone already.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            JsonElement run;
            while ((run = await GetAsync($"repos/{Repository}/actions/runs/{_runId}")).GetProperty("status").GetString() != "completed")
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"Probe run {run.GetProperty("html_url").GetString()} had not finished after five minutes, so it is not deleted; delete it on GitHub.");
                }

                await Task.Delay(Poll);
            }

            using var delete = await _api.DeleteAsync(new Uri($"repos/{Repository}/actions/runs/{_runId}", UriKind.Relative));
            if (delete.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    $"Probe run {_runId} of {Repository} could not be deleted ({(int)delete.StatusCode}): {await delete.Content.ReadAsStringAsync()}");
            }
        }
    }

    /// <summary>The run the dispatch started, found by the name the probe gives it (GitHub answers a dispatch with no id).</summary>
    private async Task<long> FindRunAsync(string nonce)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            var runs = await GetAsync($"repos/{Repository}/actions/workflows/{LiveGitHubOidc.WorkflowFile}/runs?event=workflow_dispatch&per_page=30");
            foreach (var run in runs.GetProperty("workflow_runs").EnumerateArray())
            {
                if (run.GetProperty("display_title").GetString()?.EndsWith(nonce, StringComparison.Ordinal) == true)
                {
                    return run.GetProperty("id").GetInt64();
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No run of {LiveGitHubOidc.WorkflowFile} in {Repository} named '{nonce}' appeared within three minutes of its dispatch.");
            }

            await Task.Delay(Poll);
        }
    }

    /// <summary>The token the run kept as its artifact, read as soon as the artifact exists, which is then deleted.</summary>
    private async Task<string> TakeTokenAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (true)
        {
            var artifacts = await GetAsync($"repos/{Repository}/actions/runs/{_runId}/artifacts");
            var artifact = artifacts.GetProperty("artifacts").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == ArtifactName);
            if (artifact.ValueKind == JsonValueKind.Object)
            {
                // A download that fails leaves the artifact to the run's deletion, which removes it with the run.
                var artifactId = artifact.GetProperty("id").GetInt64();
                var token = await DownloadTokenAsync(artifactId);
                using var delete = await _api!.DeleteAsync(new Uri($"repos/{Repository}/actions/artifacts/{artifactId}", UriKind.Relative));
                if (delete.StatusCode != HttpStatusCode.NoContent)
                {
                    throw new InvalidOperationException(
                        $"The probe's artifact {artifactId} in {Repository} could not be deleted ({(int)delete.StatusCode}): {await delete.Content.ReadAsStringAsync()}");
                }

                return token;
            }

            var run = await GetAsync($"repos/{Repository}/actions/runs/{_runId}");
            if (run.GetProperty("status").GetString() == "completed")
            {
                throw new InvalidOperationException(
                    $"Probe run {run.GetProperty("html_url").GetString()} ended {run.GetProperty("conclusion").GetString()} without keeping its token.");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Probe run {run.GetProperty("html_url").GetString()} kept no token within eight minutes.");
            }

            await Task.Delay(Poll);
        }
    }

    private async Task<string> DownloadTokenAsync(long artifactId)
    {
        using var redirect = await _api!.GetAsync(new Uri($"repos/{Repository}/actions/artifacts/{artifactId}/zip", UriKind.Relative));
        if (redirect.StatusCode != HttpStatusCode.Found || redirect.Headers.Location is not { } location)
        {
            throw new InvalidOperationException(
                $"GitHub answered the download of artifact {artifactId} with {(int)redirect.StatusCode}, not a redirect: {await redirect.Content.ReadAsStringAsync()}");
        }

        // The location is a signed address of its own: it is fetched without the GitHub token.
        using var storage = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var zip = new MemoryStream(await storage.GetByteArrayAsync(location));
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entry = archive.GetEntry("token")
            ?? throw new InvalidOperationException($"The probe's artifact {artifactId} holds no file named token.");
        using var reader = new StreamReader(entry.Open());
        var token = (await reader.ReadToEndAsync()).Trim();
        if (token.Split('.').Length != 3)
        {
            throw new InvalidOperationException($"The probe's artifact {artifactId} holds no JSON web token.");
        }

        return token;
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _api!.GetAsync(new Uri(path, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub answered GET {path} with {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
