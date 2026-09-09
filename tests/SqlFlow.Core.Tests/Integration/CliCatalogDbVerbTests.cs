using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The CLI verbs that talk to the shadow catalog directly (no control plane), through the compiled binary:
/// 'db migrate/status/sync' (including the create guard that refuses to conjure a database without --create),
/// 'runs cancel' dequeuing a queued run, 'user reset-password' with a piped password, and the 'worker' verb
/// draining a queued run end to end: claim, execute the flow file from the repo's root path, load the sink,
/// and record the outcome under the enqueued run id. That worker test is the full fleet loop with no control
/// plane and no GUI involved.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliCatalogDbVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_clidb_" + Guid.NewGuid().ToString("N"));
    private readonly string _dll;

    public CliCatalogDbVerbTests()
    {
        Directory.CreateDirectory(_dir);
        _dll = CliBinary.DllPath() ?? string.Empty;
    }

    private async Task<(string Dll, string Cs)> RequireAsync()
    {
        var cs = IntegrationDb.Require();
        Skip.If(_dll.Length == 0, "Built CLI not found; run 'dotnet build -c Release' first.");
        await CatalogDatabase.MigrateAsync(cs);
        return (_dll, cs);
    }

    private static (string Name, string? Value)[] CatalogEnv(string cs) => [("SQLFLOW_CATALOG_DB", cs)];

    // ---- db --------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task DbStatus_OnACurrentCatalog_Exit0_NoPending()
    {
        var (dll, cs) = await RequireAsync();

        var result = await CliBinary.RunAsync(dll, ["db", "status"], env: CatalogEnv(cs), workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("0 pending", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task DbMigrate_ExistingCatalog_Exit0()
    {
        var (dll, cs) = await RequireAsync();

        var result = await CliBinary.RunAsync(dll, ["db", "migrate"], env: CatalogEnv(cs), workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("catalog database current", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task DbMigrate_MissingDatabase_WithoutCreate_IsRefused()
    {
        var (dll, cs) = await RequireAsync();
        // Same server, a database that does not exist: without --create the guard must refuse rather than
        // silently provision it (the mistyped---db-must-never-conjure-a-catalog rule).
        var missing = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs)
        {
            InitialCatalog = "SqlFlowNoSuchDb_" + Suffix(),
        }.ConnectionString;

        var result = await CliBinary.RunAsync(dll, ["db", "migrate"], env: CatalogEnv(missing), workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("ERROR", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task DbSync_ProjectsAFlowEstateIntoTheCatalog()
    {
        var (dll, cs) = await RequireAsync();
        var repoName = "cli_dbsync_" + Suffix();
        var estate = Path.Combine(_dir, repoName);
        Directory.CreateDirectory(estate);
        var csv = Path.Combine(estate, "orders.csv");
        File.WriteAllText(csv, "Id,Name\n1,Acme\n");
        File.WriteAllText(Path.Combine(estate, "orders.flow.yaml"), $"""
            name: {repoName}_orders
            source:
              type: csv
              location: {csv.Replace('\\', '/')}
            target:
              connection: "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True"
              schema: dbo
              table: {repoName}_orders
            """);
        var repoId = FlowIdentity.FromName(repoName);

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["db", "sync", estate, "--repo", repoName], env: CatalogEnv(cs), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains("pipelines +1 added", result.StdOut, StringComparison.Ordinal);

            await using var db = CatalogDatabase.Create(cs);
            Assert.True(await db.Pipelines.AnyAsync(p => p.RepoId == repoId && p.Active),
                "the synced pipeline row is missing");
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
        }
    }

    // ---- runs cancel (direct catalog) --------------------------------------------------------------------------

    [SkippableFact]
    public async Task RunsCancel_QueuedRun_DequeuesIt_Exit0()
    {
        var (dll, cs) = await RequireAsync();
        var repoName = "cli_cancel_" + Suffix();
        var repoId = FlowIdentity.FromName(repoName);
        Guid runId;
        await using (var db = CatalogDatabase.Create(cs))
        {
            runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, repoName + "_flow", "file"), DateTime.UtcNow);
        }

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["runs", "cancel", runId.ToString()], env: CatalogEnv(cs), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains("now cancelled", result.StdOut, StringComparison.Ordinal);

            await using var db = CatalogDatabase.Create(cs);
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal("cancelled", run.Status);
        }
        finally
        {
            await CleanupRepoAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task RunsCancel_UnknownRun_Exit1()
    {
        var (dll, cs) = await RequireAsync();

        var result = await CliBinary.RunAsync(
            dll, ["runs", "cancel", Guid.NewGuid().ToString()], env: CatalogEnv(cs), workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("no run", result.StdErr, StringComparison.Ordinal);
    }

    // ---- user reset-password -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task UserResetPassword_PipedPassword_RewritesTheHash()
    {
        var (dll, cs) = await RequireAsync();
        var username = "cli-reset-" + Suffix();
        const string oldPassword = "the-old-password-123456";
        const string newPassword = "the-new-password-654321";
        await using (var db = CatalogDatabase.Create(cs))
        {
            await UserStore.EnsureRoleAsync(db, RoleNames.Operator, "read operate", "Operations.", DateTime.UtcNow);
            var probe = new CatalogUser { Username = username };
            var (status, _) = await UserStore.CreateLocalAsync(
                db, username, LocalPasswords.Hash(probe, oldPassword), RoleNames.Operator, null, null, DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.Created, status);
        }

        try
        {
            // Piped stdin is the automation path: one line, no confirmation prompt, nothing in shell history.
            var result = await CliBinary.RunAsync(
                dll, ["user", "reset-password", username], env: CatalogEnv(cs),
                stdin: newPassword + "\n", workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains("password reset", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("audit:", result.StdOut, StringComparison.Ordinal);

            await using var db = CatalogDatabase.Create(cs);
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
            var verdict = new PasswordHasher<CatalogUser>().VerifyHashedPassword(user, user.PasswordHash!, newPassword);
            Assert.NotEqual(PasswordVerificationResult.Failed, verdict);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Users.Where(u => u.Username == username).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task UserResetPassword_TooShortPipedPassword_Exit1_NothingChanged()
    {
        var (dll, cs) = await RequireAsync();
        // The verb refuses BEFORE prompting for unknown users, so the length check needs a real one.
        var username = "cli-reset-short-" + Suffix();
        const string oldPassword = "the-old-password-123456";
        string? originalHash;
        await using (var db = CatalogDatabase.Create(cs))
        {
            await UserStore.EnsureRoleAsync(db, RoleNames.Operator, "read operate", "Operations.", DateTime.UtcNow);
            var probe = new CatalogUser { Username = username };
            var (status, _) = await UserStore.CreateLocalAsync(
                db, username, LocalPasswords.Hash(probe, oldPassword), RoleNames.Operator, null, null, DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.Created, status);
            originalHash = (await db.Users.AsNoTracking().SingleAsync(u => u.Username == username)).PasswordHash;
        }

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["user", "reset-password", username], env: CatalogEnv(cs), stdin: "short\n", workingDirectory: _dir);

            Assert.Equal(1, result.Exit);
            Assert.Contains("at least", result.StdErr, StringComparison.Ordinal);

            await using var db = CatalogDatabase.Create(cs);
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
            Assert.Equal(originalHash, user.PasswordHash);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Users.Where(u => u.Username == username).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task UserResetPassword_UnknownUser_Exit1()
    {
        var (dll, cs) = await RequireAsync();

        var result = await CliBinary.RunAsync(
            dll, ["user", "reset-password", "no-such-user-" + Suffix()], env: CatalogEnv(cs),
            stdin: "a-perfectly-long-password-1\n", workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("no user", result.StdErr, StringComparison.Ordinal);
    }

    // ---- worker -------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Worker_DrainsAQueuedRun_ExecutesTheFlow_AndRecordsSuccess()
    {
        var (dll, cs) = await RequireAsync();
        var suffix = Suffix();
        var repoName = "cli_worker_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cli_worker_orders_" + suffix;
        var table = "IT_CliWrk_" + suffix;
        await IntegrationDb.DropTableAsync(cs, table);

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
            runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "file"), now);
        }

        await using var worker = CliBinary.Start(
            dll, ["worker", "--poll-seconds", "1"],
            env: [("SQLFLOW_CATALOG_DB", cs), ("SQLFlowSinkConStr", cs)], workingDirectory: _dir);
        try
        {
            var banner = await worker.WaitForOutputLineAsync(
                l => l.Contains("draining the run queue", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.True(banner is not null, $"the worker never announced itself.\n{worker.StdOut}\n{worker.StdErr}");

            // The queue's poll is 1s and the flow is tiny; 90s is generous headroom for a cold first claim.
            var deadline = DateTime.UtcNow.AddSeconds(90);
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
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
            await CleanupRepoAsync(cs, repoId);
        }
    }

    private static async Task CleanupRepoAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
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
