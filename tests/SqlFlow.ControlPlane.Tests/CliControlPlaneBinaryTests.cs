using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Cli.Remote;
using SqlFlow.Core.Identity;
using SqlFlow.Tests.Integration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The CLI's control-plane verbs end to end through REAL processes: the compiled sqlflow binary talking real
/// HTTP (and SSE) to the compiled control plane hosted on a real Kestrel port, against the real test catalog.
/// This is the whole operator chain with no in-memory shortcuts: login (password, pasted token, and the
/// browser device grant), credential storage and revocation, trigger across scopes with preview and follow,
/// the runs family, and run groups. Every test isolates its credential file via SQLFLOW_CREDENTIALS_FILE and
/// its catalog rows via unique names. This file deliberately avoids importing SqlFlow.ControlPlane.Api: the
/// CLI's own mirror records are the contract under test.
/// </summary>
[Collection("cli-control-plane")]
[Trait("Category", "Integration")]
public sealed class CliControlPlaneBinaryTests : IDisposable
{
    private readonly ControlPlaneProcessFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_clicp_" + Guid.NewGuid().ToString("N"));

    public CliControlPlaneBinaryTests(ControlPlaneProcessFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Gates on the catalog and both built binaries, then starts (or reuses) the control plane.</summary>
    private async Task<(string Dll, string Url, string Cs)> RequireAsync()
    {
        var cs = CatalogTestDb.Require();
        var dll = CliBinary.DllPath();
        Skip.If(dll is null, "Built CLI not found; run 'dotnet build -c Release' first.");
        var url = await _fixture.EnsureStartedAsync(cs);
        return (dll!, url, cs);
    }

    private (string Name, string? Value)[] CliEnv(string url, params (string Name, string? Value)[] extra)
    {
        (string, string?)[] baseline =
        [
            ("SQLFLOW_URL", url),
            ("SQLFLOW_CREDENTIALS_FILE", Path.Combine(_dir, "credentials.json")),
        ];
        return [.. baseline, .. extra];
    }

    // ---- health -------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Health_AgainstTheRealControlPlane_Exit0()
    {
        var (dll, url, _) = await RequireAsync();

        var result = await CliBinary.RunAsync(dll, ["health"], env: CliEnv(url), workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("live:  200", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("ready: 200", result.StdOut, StringComparison.Ordinal);
    }

    // ---- login / logout -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Login_Password_StoresAPat_RunsListWorks_LogoutRevokesIt()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var credentialFile = Path.Combine(_dir, "credentials.json");

        try
        {
            var login = await CliBinary.RunAsync(
                dll, ["login", "--username", username], env: CliEnv(url), stdin: password + "\n", workingDirectory: _dir);
            Assert.True(login.Exit == 0, login.AllOutput);
            Assert.Contains($"signed in to {url}", login.StdOut, StringComparison.Ordinal);
            Assert.Contains("stored in", login.StdOut, StringComparison.Ordinal);

            // The stored credential is a revocable PAT (never the password, never the expiring JWT).
            var stored = File.ReadAllText(credentialFile);
            Assert.Contains("sqlf_", stored, StringComparison.Ordinal);
            Assert.DoesNotContain(password, stored, StringComparison.Ordinal);

            var list = await CliBinary.RunAsync(dll, ["runs", "list"], env: CliEnv(url), workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
            Assert.Contains("run(s), page", list.StdOut, StringComparison.Ordinal);

            var logout = await CliBinary.RunAsync(dll, ["logout"], env: CliEnv(url), workingDirectory: _dir);
            Assert.True(logout.Exit == 0, logout.AllOutput);
            Assert.Contains("revoked", logout.StdOut, StringComparison.Ordinal);

            // Nothing stored anymore, so the next call is anonymous and the server's 401 comes back as exit 1.
            var locked = await CliBinary.RunAsync(dll, ["runs", "list"], env: CliEnv(url), workingDirectory: _dir);
            Assert.Equal(1, locked.Exit);
            Assert.Contains("401", locked.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Login_WrongPassword_Exit1()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, _) = await SeedOperatorAsync(cs);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["login", "--username", username], env: CliEnv(url),
                stdin: "definitely-not-the-password-9\n", workingDirectory: _dir);

            Assert.Equal(1, result.Exit);
            Assert.Contains("401", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Login_NoStore_PrintsTheSecretOnce_UsableViaTokenFlag()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);

        try
        {
            var login = await CliBinary.RunAsync(
                dll, ["login", "--username", username, "--no-store"], env: CliEnv(url),
                stdin: password + "\n", workingDirectory: _dir);
            Assert.True(login.Exit == 0, login.AllOutput);
            var secret = login.StdOut.Split('\n').Select(l => l.Trim())
                .Single(l => l.StartsWith("sqlf_", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(_dir, "credentials.json")), "--no-store must not write the file");

            var list = await CliBinary.RunAsync(
                dll, ["runs", "list", "--token", secret], env: CliEnv(url), workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Login_WithPastedToken_ValidatesMatchesItsId_AndStoresIt()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);

        try
        {
            // Mint a PAT the way an operator would have (session sign-in, then a token), through the same
            // typed client the CLI itself uses.
            using var client = new ControlPlaneClient(new Uri(url));
            var session = await client.LoginAsync(username, password, CancellationToken.None);
            client.BearerToken = session.AccessToken;
            var created = await client.CreateAccessTokenAsync("pasted@tests", null, 1, CancellationToken.None);

            var login = await CliBinary.RunAsync(
                dll, ["login", "--with-token"], env: CliEnv(url), stdin: created.Secret + "\n", workingDirectory: _dir);
            Assert.True(login.Exit == 0, login.AllOutput);
            Assert.Contains("'pasted@tests'", login.StdOut, StringComparison.Ordinal);

            var list = await CliBinary.RunAsync(dll, ["runs", "list"], env: CliEnv(url), workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);

            // logout can revoke it server-side because --with-token matched the pasted secret to its id.
            var logout = await CliBinary.RunAsync(dll, ["logout"], env: CliEnv(url), workingDirectory: _dir);
            Assert.True(logout.Exit == 0, logout.AllOutput);
            Assert.Contains("revoked", logout.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Login_DeviceGrant_ApprovedFromTheBrowserSide_StoresAPat()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);

        try
        {
            await using var login = CliBinary.Start(dll, ["login", "--device"], env: CliEnv(url), workingDirectory: _dir);
            var codeLine = await login.WaitForOutputLineAsync(
                l => l.Contains("confirm code", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.True(codeLine is not null, $"the device code never appeared.\n{login.StdOut}\n{login.StdErr}");
            var userCode = codeLine!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

            // The "browser side": a signed-in user approves the code, exactly what the /device page posts.
            using var client = new ControlPlaneClient(new Uri(url));
            var session = await client.LoginAsync(username, password, CancellationToken.None);
            using var http = new HttpClient();
            using var approve = new HttpRequestMessage(HttpMethod.Post, new Uri(url + "/api/v1/auth/device/approve"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { userCode }), Encoding.UTF8, "application/json"),
            };
            approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var approval = await http.SendAsync(approve);
            Assert.True(approval.IsSuccessStatusCode, $"approve answered {(int)approval.StatusCode}");

            var exit = await login.WaitForExitAsync(TimeSpan.FromSeconds(60));
            Assert.True(exit == 0, $"login --device exited {exit}.\n{login.StdOut}\n{login.StdErr}");
            Assert.Contains("sqlf_", File.ReadAllText(Path.Combine(_dir, "credentials.json")), StringComparison.Ordinal);

            var list = await CliBinary.RunAsync(dll, ["runs", "list"], env: CliEnv(url), workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    // ---- trigger / runs / groups --------------------------------------------------------------------------

    [SkippableFact]
    public async Task Trigger_FlowScope_ThenShowTraceListCancel()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, batch) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var trigger = await CliBinary.RunAsync(
                dll, ["trigger", "--repo", repoName, "--flow", flowName, "--json"], env: env, workingDirectory: _dir);
            Assert.True(trigger.Exit == 0, trigger.AllOutput);
            using var accepted = JsonDocument.Parse(trigger.StdOut);
            var runId = accepted.RootElement.GetProperty("runId").GetGuid();
            Assert.Equal("queued", accepted.RootElement.GetProperty("status").GetString());

            var show = await CliBinary.RunAsync(
                dll, ["runs", "show", runId.ToString(), "--json"], env: env, workingDirectory: _dir);
            Assert.True(show.Exit == 0, show.AllOutput);
            using var detail = JsonDocument.Parse(show.StdOut);
            Assert.Equal(flowName, detail.RootElement.GetProperty("flowName").GetString());
            Assert.Equal(batch, detail.RootElement.GetProperty("batch").GetString());

            var list = await CliBinary.RunAsync(
                dll, ["runs", "list", "--flow", flowName, "--json"], env: env, workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
            using var page = JsonDocument.Parse(list.StdOut);
            Assert.True(page.RootElement.GetProperty("total").GetInt64() >= 1);

            var trace = await CliBinary.RunAsync(
                dll, ["runs", "trace", runId.ToString()], env: env, workingDirectory: _dir);
            Assert.True(trace.Exit == 0, trace.AllOutput);

            // The run races the host's dispatcher, so any lifecycle-consistent cancel answer is legitimate;
            // the contract is that the CLI reports it faithfully with the matching exit code.
            var cancel = await CliBinary.RunAsync(
                dll, ["runs", "cancel", runId.ToString()], env: env, workingDirectory: _dir);
            if (cancel.Exit == 0)
            {
                Assert.Contains("cancel", cancel.StdOut, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Contains("already finished", cancel.StdErr, StringComparison.Ordinal);
            }
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Trigger_Preview_NodeScope_ListsTheMembersWithoutEnqueuing()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var preview = await CliBinary.RunAsync(
                dll,
                ["trigger", "--repo", repoName, "--flow", flowName, "--scope", "node", "--preview"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.True(preview.Exit == 0, preview.AllOutput);
            Assert.Contains(flowName, preview.StdOut, StringComparison.Ordinal);
            Assert.Contains("wave", preview.StdOut, StringComparison.Ordinal);

            await using var db = CatalogDatabase.Create(cs);
            Assert.False(await db.Runs.AnyAsync(r => r.RepoId == repoId), "preview must not enqueue");

            // There is no ad-hoc batch scope anymore: a whole source runs through its schedule, and the CLI
            // answers the retired spelling with guidance instead of a server round trip.
            var batchScope = await CliBinary.RunAsync(
                dll,
                ["trigger", "--repo", repoName, "--scope", "batch", "--preview"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);
            Assert.Equal(1, batchScope.Exit);
            Assert.Contains("schedule", batchScope.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Trigger_NodeScope_GroupShowAndRerun()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var trigger = await CliBinary.RunAsync(
                dll, ["trigger", "--repo", repoName, "--flow", flowName, "--scope", "node", "--json"],
                env: env, workingDirectory: _dir);
            Assert.True(trigger.Exit == 0, trigger.AllOutput);
            using var accepted = JsonDocument.Parse(trigger.StdOut);
            var groupId = accepted.RootElement.GetProperty("groupId").GetGuid();
            Assert.True(accepted.RootElement.GetProperty("memberCount").GetInt32() >= 1);

            var show = await CliBinary.RunAsync(
                dll, ["groups", "show", groupId.ToString()], env: env, workingDirectory: _dir);
            Assert.True(show.Exit == 0, show.AllOutput);
            Assert.Contains("member", show.StdOut, StringComparison.Ordinal);
            Assert.Contains(flowName, show.StdOut, StringComparison.Ordinal);

            // Rerun re-expands the same anchor freshly and mints a NEW group.
            var rerun = await CliBinary.RunAsync(
                dll, ["groups", "rerun", groupId.ToString(), "--json"], env: env, workingDirectory: _dir);
            Assert.True(rerun.Exit == 0, rerun.AllOutput);
            using var rerunAccepted = JsonDocument.Parse(rerun.StdOut);
            Assert.NotEqual(groupId, rerunAccepted.RootElement.GetProperty("groupId").GetGuid());
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Trigger_Follow_StreamsToTheTerminalOutcome_AndExitsByIt()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        // The pipeline's flow file does not exist on disk, so the dispatcher must fail the run; --follow
        // must ride the live SSE trace to that terminal state and exit non-zero because the run did not
        // succeed. That proves the whole loop: enqueue, dispatch, stream, terminal detail.
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["trigger", "--repo", repoName, "--flow", flowName, "--follow"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir, timeoutSeconds: 180);

            Assert.Equal(1, result.Exit);
            Assert.Contains("run ", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Trigger_UnknownRepo_Exit1_NamesTheKnownRepos()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["trigger", "--repo", "no-such-repo-anywhere", "--flow", "f"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.Equal(1, result.Exit);
            Assert.Contains("no repo named 'no-such-repo-anywhere'", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task RunsShow_UnknownRun_Exit1()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["runs", "show", Guid.NewGuid().ToString()],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.Equal(1, result.Exit);
            Assert.Contains("no run", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Trigger_WithReadOnlyToken_Succeeds_BecauseRunningIsNotScopeGated()
    {
        // The privilege model has two tiers: any authenticated caller may run flows, and only user administration is
        // scope-gated. So a token minted with just the read scope triggers a run rather than being turned away for
        // lacking "operate".
        var (dll, url, cs) = await RequireAsync();
        var username = "cli-cp-viewer-" + Suffix();
        const string password = "a-long-viewer-password-12";
        await SeedUserAsync(cs, username, password, RoleNames.Viewer, "read");
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["trigger", "--repo", repoName, "--flow", flowName, "--json"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            using var accepted = JsonDocument.Parse(result.StdOut);
            Assert.Equal("queued", accepted.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    // ---- estate verbs (whoami, summary, nodes, schedules, repos, pipelines, search, lineage, datasources) ---

    [SkippableFact]
    public async Task Whoami_WithPat_ReportsSubjectScopesAndSource()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["whoami"], env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains(username, result.StdOut, StringComparison.Ordinal);
            Assert.Contains("operate", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("SQLFLOW_TOKEN", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Summary_And_Nodes_RenderTheOperateSurfaces()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var summary = await CliBinary.RunAsync(dll, ["summary", "--json"], env: env, workingDirectory: _dir);
            Assert.True(summary.Exit == 0, summary.AllOutput);
            using var dashboard = JsonDocument.Parse(summary.StdOut);
            Assert.True(dashboard.RootElement.GetProperty("pipelines").GetInt64() >= 0);

            var nodes = await CliBinary.RunAsync(dll, ["nodes"], env: env, workingDirectory: _dir);
            Assert.True(nodes.Exit == 0, nodes.AllOutput);
            Assert.Contains("node(s)", nodes.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Schedules_FullLifecycle_CreatePauseResumeDelete()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var create = await CliBinary.RunAsync(
                dll, ["schedules", "create", "--repo", repoName, "--flow", flowName, "--interval", "3600", "--json"],
                env: env, workingDirectory: _dir);
            Assert.True(create.Exit == 0, create.AllOutput);
            using var created = JsonDocument.Parse(create.StdOut);
            var scheduleId = created.RootElement.GetProperty("id").GetGuid();

            var list = await CliBinary.RunAsync(
                dll, ["schedules", "list", "--repo", repoName], env: env, workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
            Assert.Contains(flowName, list.StdOut, StringComparison.Ordinal);
            Assert.Contains("every 3600s", list.StdOut, StringComparison.Ordinal);

            var pause = await CliBinary.RunAsync(
                dll, ["schedules", "pause", scheduleId.ToString()], env: env, workingDirectory: _dir);
            Assert.True(pause.Exit == 0, pause.AllOutput);
            Assert.Contains("paused", pause.StdOut, StringComparison.Ordinal);

            var resume = await CliBinary.RunAsync(
                dll, ["schedules", "resume", scheduleId.ToString()], env: env, workingDirectory: _dir);
            Assert.True(resume.Exit == 0, resume.AllOutput);
            Assert.Contains("resumed", resume.StdOut, StringComparison.Ordinal);

            var delete = await CliBinary.RunAsync(
                dll, ["schedules", "delete", scheduleId.ToString()], env: env, workingDirectory: _dir);
            Assert.True(delete.Exit == 0, delete.AllOutput);

            var gone = await CliBinary.RunAsync(
                dll, ["schedules", "show", scheduleId.ToString()], env: env, workingDirectory: _dir);
            Assert.Equal(1, gone.Exit);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Schedules_Create_RequiresExactlyOneTrigger()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var neither = await CliBinary.RunAsync(
                dll, ["schedules", "create", "--repo", "whatever", "--flow", "f"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);
            Assert.Equal(1, neither.Exit);
            Assert.Contains("exactly one of --cron", neither.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Repos_ListAndShow_MergeTheSourceView()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, _, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var list = await CliBinary.RunAsync(dll, ["repos", "list"], env: env, workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
            Assert.Contains(repoName, list.StdOut, StringComparison.Ordinal);

            var show = await CliBinary.RunAsync(dll, ["repos", "show", repoName], env: env, workingDirectory: _dir);
            Assert.True(show.Exit == 0, show.AllOutput);
            using var detail = JsonDocument.Parse(show.StdOut);
            Assert.Equal(repoName, detail.RootElement.GetProperty("repo").GetProperty("name").GetString());
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Repos_Register_RefusesARawSecretAsCredential()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll,
                ["repos", "register", "--name", "x", "--remote-url", "https://example/x.git", "--credential-ref", "ghp_rawtoken123456789012345678901234"],
                env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            // Refused either client-side (the hygiene check) or server-side (the endpoint's 400); both are
            // the same contract: a raw token never lands in the catalog.
            Assert.Equal(1, result.Exit);
            Assert.True(
                result.StdErr.Contains("REFERENCE", StringComparison.Ordinal)
                || result.StdErr.Contains("400", StringComparison.Ordinal),
                result.StdErr);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Pipelines_ListShowYamlColumns_ExposeTheRegistry()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var list = await CliBinary.RunAsync(
                dll, ["pipelines", "list", "--repo", repoName, "--json"], env: env, workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);
            using var page = JsonDocument.Parse(list.StdOut);
            var pipeline = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
            var pipelineId = pipeline.GetProperty("id").GetGuid();

            // --yaml puts the raw document on stdout, nothing else: the pipe-friendly contract.
            var yaml = await CliBinary.RunAsync(
                dll, ["pipelines", "show", pipelineId.ToString(), "--yaml"], env: env, workingDirectory: _dir);
            Assert.True(yaml.Exit == 0, yaml.AllOutput);
            Assert.Contains("flowType: ing", yaml.StdOut, StringComparison.Ordinal);
            Assert.Contains($"name: {flowName}", yaml.StdOut, StringComparison.Ordinal);

            var columns = await CliBinary.RunAsync(
                dll, ["pipelines", "columns", pipelineId.ToString()], env: env, workingDirectory: _dir);
            Assert.True(columns.Exit == 0, columns.AllOutput);
            Assert.Contains("column(s)", columns.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Search_FlowsCategory_FindsTheSeededFlow()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["search", flowName, "--flows"], env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains(flowName, result.StdOut, StringComparison.Ordinal);
            Assert.Contains(repoName, result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Lineage_Waves_PrintTheExecutionPlanAsData()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var (repoId, repoName, flowName, _) = await SeedRepoPipelineAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["lineage", "waves", "--repo", repoName], env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains(flowName, result.StdOut, StringComparison.Ordinal);
            Assert.Contains("wave", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Datasources_TaskLifecycle_NoWaitThenCancel()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        // The datasource reference must be one the estate declares; a pipeline whose definition carries a
        // connections block makes '${env:CLI_CP_DS}' resolvable as a task target.
        var suffix = Suffix();
        var repoName = "cli_cp_ds_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cli_cp_ds_flow_" + suffix;
        var now = DateTime.UtcNow;
        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = repoName, RootPath = Path.Combine(Path.GetTempPath(), repoName),
                FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, flowName),
                RepoId = repoId, Name = flowName, Kind = "ing",
                RelativePath = "flows/" + flowName + ".flow.yaml",
                ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
                Yaml = $"name: {flowName}\nflowType: ing\n",
                DefinitionJson =
                    $$$"""{"name":"{{{flowName}}}","flowType":"ing","connections":{"src":"${env:CLI_CP_DS}"},"source":{"server":"src","object":"Db.dbo.A"},"target":{"server":"src","object":"Db.dbo.B"}}""",
                SourceServer = "${env:CLI_CP_DS}", TargetServer = "${env:CLI_CP_DS}",
                Active = true, Wave = 0, FirstSeenUtc = now, LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var token = await MintPatAsync(url, username, password);
        var env = CliEnv(url, ("SQLFLOW_TOKEN", token));

        try
        {
            var list = await CliBinary.RunAsync(dll, ["datasources", "list"], env: env, workingDirectory: _dir);
            Assert.True(list.Exit == 0, list.AllOutput);

            // Queue without waiting (no worker serves this catalog), then drive the queue surface: the task
            // is listed, showable, and cancellable, exactly what the GUI's task page does.
            var queued = await CliBinary.RunAsync(
                dll, ["datasources", "test", "--ref", "${env:CLI_CP_DS}", "--no-wait"], env: env, workingDirectory: _dir);
            Assert.True(queued.Exit == 0, queued.AllOutput);
            var taskId = Guid.Parse(queued.StdOut.Trim().Split('\n')[^1].Trim());

            var tasks = await CliBinary.RunAsync(dll, ["datasources", "tasks"], env: env, workingDirectory: _dir);
            Assert.True(tasks.Exit == 0, tasks.AllOutput);
            Assert.Contains(taskId.ToString(), tasks.StdOut, StringComparison.Ordinal);

            // The task races whatever drains the compute queue (a worker may fail the unresolvable reference
            // within milliseconds), so any lifecycle-consistent cancel answer is legitimate; the CLI's
            // contract is the faithful mapping, exactly like run cancellation.
            var cancel = await CliBinary.RunAsync(
                dll, ["datasources", "cancel", taskId.ToString()], env: env, workingDirectory: _dir);
            if (cancel.Exit == 0)
            {
                Assert.Contains("cancel", cancel.StdOut, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Contains("already finished", cancel.StdErr, StringComparison.Ordinal);
            }

            // 'task <id>' always shows the authoritative record; exit 1 only for the failed status.
            var show = await CliBinary.RunAsync(
                dll, ["datasources", "task", taskId.ToString()], env: env, workingDirectory: _dir);
            using var record = JsonDocument.Parse(show.StdOut);
            Assert.Equal(taskId, record.RootElement.GetProperty("taskId").GetGuid());
            var status = record.RootElement.GetProperty("status").GetString();
            Assert.Contains(status, new[] { "queued", "running", "succeeded", "failed", "cancelled" });
            Assert.Equal(status == "failed" ? 1 : 0, show.Exit);
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == "${env:CLI_CP_DS}").ExecuteDeleteAsync();
            }

            await CleanupRepoAsync(cs, repoId);
            await CleanupUserAsync(cs, username);
        }
    }

    [SkippableFact]
    public async Task Doctor_WithUrlAndToken_ReportsTheControlPlaneAndCredential()
    {
        var (dll, url, cs) = await RequireAsync();
        var (username, password) = await SeedOperatorAsync(cs);
        var token = await MintPatAsync(url, username, password);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["doctor"], env: CliEnv(url, ("SQLFLOW_TOKEN", token)), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains("control plane: OK", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("credential:    OK", result.StdOut, StringComparison.Ordinal);
            Assert.Contains(username, result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupUserAsync(cs, username);
        }
    }

    // ---- seeding and cleanup ------------------------------------------------------------------------------

    // ---- worker ---------------------------------------------------------------------------------------------

    /// <summary>The whole fleet loop through real processes: a queued run routed to a pool only the standalone
    /// worker serves, the compiled worker polling the compiled control plane over HTTP with a node token and NO
    /// catalog connection, executing the flow file from the repo's root path (carried in the hand-out's spec),
    /// loading the sink, streaming the trace, and reporting the outcome under the enqueued run id. The control
    /// plane's own in-process node serves no pool, so it never takes the run.</summary>
    [SkippableFact]
    public async Task Worker_PollsTheControlPlane_ExecutesTheFlow_AndReportsSuccess()
    {
        var (dll, url, cs) = await RequireAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cli_worker_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cli_worker_orders_" + suffix;
        var pool = "cli-worker-" + suffix;
        var table = "IT_CliWrk_" + suffix;
        await DropTableAsync(cs, table);

        // The repo's working tree on disk: the worker resolves an unpinned run from RootPath + RelativePath.
        var root = Path.Combine(_dir, repoName);
        Directory.CreateDirectory(Path.Combine(root, "flows"));
        var csv = Path.Combine(root, "flows", "orders.csv");
        File.WriteAllText(csv, "Id,Name\n1,Acme\n2,Globex\n");
        File.WriteAllText(Path.Combine(root, "flows", flowName + ".flow.yaml"), $$"""
            name: {{flowName}}
            source:
              type: csv
              location: {{csv.Replace('\\', '/')}}
            target:
              connection: "${env:SQLFlowSinkConStr}"
              schema: dbo
              table: {{table}}
            """);

        Guid runId;
        var now = DateTime.UtcNow;
        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId,
                Name = repoName,
                RootPath = root,
                FirstSeenUtc = now,
                LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, flowName),
                RepoId = repoId,
                Name = flowName,
                Kind = "file",
                RelativePath = "flows/" + flowName + ".flow.yaml",
                ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
                Yaml = "name: " + flowName,
                DefinitionJson = $$"""{"name":"{{flowName}}"}""",
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
            // Journaled straight into the catalog, bypassing the control plane's notify: the dispatcher's reconcile
            // is what picks it up, which is the same path an enqueue on a passive replica takes.
            runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "file", pool), now)).RunId;
        }

        var nodeToken = await MintBootstrapTokenAsync(url, ["node"]);
        await using var worker = CliBinary.Start(
            dll, ["worker", "--pool", pool, "--poll-seconds", "5"],
            env: CliEnv(url, ("SQLFLOW_TOKEN", nodeToken), ("SQLFlowSinkConStr", cs)),
            workingDirectory: _dir);
        try
        {
            var banner = await worker.WaitForOutputLineAsync(
                l => l.Contains("polling", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.True(banner is not null, $"the worker never announced itself.\n{worker.StdOut}\n{worker.StdErr}");

            // Reconcile runs every few seconds and the flow is tiny; 120s is generous headroom for a cold start.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            string status;
            do
            {
                await Task.Delay(1000);
                await using var db = CatalogDatabase.Create(cs);
                status = (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId)).Status;
            }
            while (status is "queued" or "running" && DateTime.UtcNow < deadline);

            Assert.True(status == "succeeded",
                $"run ended '{status}'.\nworker stdout:\n{worker.StdOut}\nworker stderr:\n{worker.StdErr}");
            Assert.Equal(2, await RowCountAsync(cs, table));
            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal(Environment.MachineName, run.ClaimedByNode);
                Assert.Equal(1, run.Attempt);
                // The worker was started with no catalog connection at all, so the run's live trace can only have
                // reached the catalog over the node protocol: its statements and events are there under the run.
                Assert.True(await db.RunStatements.AsNoTracking().AnyAsync(s => s.RunId == runId), "no live statements reached the catalog");
                Assert.True(await db.RunEvents.AsNoTracking().AnyAsync(e => e.RunId == runId), "no live events reached the catalog");
            }
        }
        finally
        {
            await DropTableAsync(cs, table);
            await CleanupRepoAsync(cs, repoId);
            await using var db = CatalogDatabase.Create(cs);
            await db.Nodes.Where(n => n.Name == Environment.MachineName).ExecuteDeleteAsync();
        }
    }

    private static async Task<string> MintBootstrapTokenAsync(string url, IReadOnlyList<string> scopes)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        using var response = await http.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new { secret = ControlPlaneAppFactory.BootstrapSecret, scopes });
        response.EnsureSuccessStatusCode();
        // The bootstrap token endpoint answers in the API's camelCase shape (the RFC 8628 device grant is the one
        // that uses snake_case); the CLI's own mirror record is deliberately not imported here.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task DropTableAsync(string cs, string table)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF OBJECT_ID('dbo.[{table}]', 'U') IS NOT NULL DROP TABLE dbo.[{table}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> RowCountAsync(string cs, string table)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}];";
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<(string Username, string Password)> SeedOperatorAsync(string cs)
    {
        var username = "cli-cp-op-" + Suffix();
        const string password = "a-long-operator-password-1";
        await SeedUserAsync(cs, username, password, RoleNames.Operator, "read operate");
        return (username, password);
    }

    private static async Task SeedUserAsync(string cs, string username, string password, string role, string scopes)
    {
        await using var db = CatalogDatabase.Create(cs);
        await UserStore.EnsureRoleAsync(db, role, scopes, role + " (cli tests).", DateTime.UtcNow);
        var hash = new PasswordHasher<CatalogUser>().HashPassword(new CatalogUser { Username = username }, password);
        var (status, _) = await UserStore.CreateLocalAsync(db, username, hash, role, null, null, DateTime.UtcNow);
        Assert.Equal(UserCreateStatus.Created, status);
    }

    /// <summary>Signs in and mints a PAT through the CLI's own typed client (the operator's programmatic path).</summary>
    private static async Task<string> MintPatAsync(string url, string username, string password)
    {
        using var client = new ControlPlaneClient(new Uri(url));
        var session = await client.LoginAsync(username, password, CancellationToken.None);
        client.BearerToken = session.AccessToken;
        var created = await client.CreateAccessTokenAsync("cli-suite@tests", null, 1, CancellationToken.None);
        return created.Secret;
    }

    private static async Task<(Guid RepoId, string RepoName, string FlowName, string Batch)> SeedRepoPipelineAsync(string cs)
    {
        var suffix = Suffix();
        var repoName = "cli_cp_repo_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cli_cp_orders_" + suffix;
        var batch = "cli_cp_batch_" + suffix;
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
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
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "ing",
            Batch = batch,
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
        return (repoId, repoName, flowName, batch);
    }

    private static async Task CleanupRepoAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    private static async Task CleanupUserAsync(string cs, string username)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Users.Where(u => u.Username == username).ExecuteDeleteAsync();
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
