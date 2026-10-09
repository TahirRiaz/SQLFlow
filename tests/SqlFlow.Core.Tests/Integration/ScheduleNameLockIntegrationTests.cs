using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A catalog sync and an API schedule create decide what a repo's schedule names hold under one lock
/// (<see cref="ScheduleStore.LockScheduleNamesAsync"/>), taken first in both, so neither can land between the other's
/// check of a name and its write of it. Both orders are driven against SQL Server with the real code: an API create
/// arriving after the sync has read the names (the sync is held, by a lock the test takes on its repo row, right after
/// that read) waits for the sync, which commits, and is then refused naming the git schedule; an API create in flight
/// when the sync starts is waited for and seen by the sync, which leaves its git schedule out with a warning and
/// commits. Without the lock the first order failed the sync on the unique (repo, name) index.
/// <para>The class runs alone (<see cref="ScheduleNameLockGroup"/>): it holds catalog rows open on purpose, which would
/// stall other tests' syncs, and their syncs, deadlocking with this one's under SERIALIZABLE, would make the catalog
/// retry it and change the very order these tests pin.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(ScheduleNameLockGroup.Name)]
public sealed class ScheduleNameLockIntegrationTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_schedlock_" + Guid.NewGuid().ToString("N"));

    public ScheduleNameLockIntegrationTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task AnApiCreate_BetweenTheSyncsReadAndItsWrite_WaitsForTheSync_ThenIsRefusedNamingTheGitSchedule()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repo, repoId, flow, name) = NewEstate();
        try
        {
            await SyncAsync(cs, repo);
            WriteFlow(flow, $"schedule:\n  name: {name}\n  cron: \"0 2 * * *\"\n");

            // Hold the repo row against writes: the sync takes the name lock, reads the names, and then blocks here, at its
            // first write of the row. An update lock rather than an exclusive one, so the reads the sync makes of the row
            // before its transaction opens (its stored subscriber fingerprint) still pass, as they do in production.
            await using var holder = new SqlConnection(cs);
            await holder.OpenAsync();
            var holderSession = await SessionIdAsync(holder);
            await using var hold = (SqlTransaction)await holder.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await using (var command = new SqlCommand(
                             "SELECT [Id] FROM [catalog].[Repo] WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE [Id] = @id;", holder, hold))
            {
                command.Parameters.AddWithValue("@id", repoId);
                await command.ExecuteScalarAsync();
            }

            // Each side runs under an application name of its own, so the waits below watch exactly these two sessions
            // and never a session of another test that happens to touch the same tables.
            var syncApp = "schedlock-sync-" + repoId.ToString("N")[..8];
            var apiApp = "schedlock-api-" + repoId.ToString("N")[..8];
            var sync = Task.Run(() => SyncAsync(Named(cs, syncApp), repo));
            var syncSession = await BlockedByAsync(cs, syncApp, holderSession);

            // The API create arrives now, between the sync's read of the names and its write of the git schedule.
            var create = Task.Run(() => CreateApiAsync(Named(cs, apiApp), repoId, name, flow));
            await BlockedByAsync(cs, apiApp, syncSession);
            Assert.False(create.IsCompleted, "the API create must wait for the sync that holds the names");

            await hold.RollbackAsync();

            var result = await sync.WaitAsync(Patience);
            Assert.DoesNotContain(result.Warnings, w => w.Contains("is not synced", StringComparison.Ordinal));
            var refused = await Assert.ThrowsAsync<ScheduleNameTakenException>(() => create.WaitAsync(Patience));
            Assert.Equal("yaml", refused.ExistingSource);
            Assert.Equal(CatalogIdentity.YamlSchedule(repoId, name), refused.ExistingId);

            await using var check = CatalogDatabase.Create(cs);
            var schedule = Assert.Single(await check.Schedules.AsNoTracking().Where(s => s.RepoId == repoId).ToListAsync());
            Assert.Equal("yaml", schedule.Source);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task AnApiCreate_InFlightWhenTheSyncStarts_IsWaitedFor_AndTheSyncWarnsAndCommits()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repo, repoId, flow, name) = NewEstate();
        try
        {
            await SyncAsync(cs, repo);
            WriteFlow(flow, $"schedule:\n  name: {name}\n  cron: \"0 2 * * *\"\n");

            // An API create in flight: the name lock taken and its schedule written, not yet committed. Its steps are
            // the store's, held open by a transaction the test owns, on a context without the retrying strategy (which
            // forbids a transaction it does not run itself).
            await using var api = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(cs).Options);
            await using var apiTransaction = await api.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var apiSession = await SessionIdAsync((SqlConnection)api.Database.GetDbConnection(), apiTransaction.GetDbTransaction());
            await ScheduleStore.LockScheduleNamesAsync(api, repoId);
            var apiId = Guid.CreateVersion7();
            var now = DateTime.UtcNow;
            api.Schedules.Add(new CatalogSchedule
            {
                Id = apiId,
                RepoId = repoId,
                Name = name.ToUpperInvariant(),
                Cron = "0 6 * * *",
                Timezone = "UTC",
                Enabled = true,
                Source = "api",
                NextFireUtc = now.AddHours(1),
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            await api.SaveChangesAsync();

            var syncApp = "schedlock-sync-" + repoId.ToString("N")[..8];
            var sync = Task.Run(() => SyncAsync(Named(cs, syncApp), repo));
            await BlockedByAsync(cs, syncApp, apiSession);
            Assert.False(sync.IsCompleted, "the sync must wait for the API create that holds the names");

            await apiTransaction.CommitAsync();

            var result = await sync.WaitAsync(Patience);
            Assert.Contains(result.Warnings, w => w.StartsWith($"schedule '{name}' ('{flow}' ({flow}.yaml)) is not synced", StringComparison.Ordinal)
                                                  && w.Contains($"(id {apiId})", StringComparison.Ordinal));
            await using var check = CatalogDatabase.Create(cs);
            var schedule = Assert.Single(await check.Schedules.AsNoTracking().Where(s => s.RepoId == repoId).ToListAsync());
            Assert.Equal(apiId, schedule.Id);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task TheNameLock_BelongsToATransaction()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ScheduleStore.LockScheduleNamesAsync(db, Guid.NewGuid()));
    }

    private (string Repo, Guid RepoId, string Flow, string Name) NewEstate()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "schedlock_" + suffix;
        var flow = "orders_" + suffix;
        WriteFlow(flow, string.Empty);
        return (repo, FlowIdentity.FromName(repo), flow, "nightly_" + suffix);
    }

    private async Task<CatalogSyncResult> SyncAsync(string cs, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
    }

    private static async Task<Guid> CreateApiAsync(string cs, Guid repoId, string name, string flow)
    {
        await using var db = CatalogDatabase.Create(cs);
        var now = DateTime.UtcNow;
        return await ScheduleStore.CreateApiScheduleAsync(
            db, repoId, name, [flow], "0 6 * * *", null, "UTC", enabled: true, catchup: false,
            maxConcurrency: null, now.AddHours(1), now);
    }

    private static async Task<short> SessionIdAsync(SqlConnection connection, System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = new SqlCommand("SELECT CAST(@@SPID AS smallint);", connection, (SqlTransaction?)transaction);
        return (short)(await command.ExecuteScalarAsync())!;
    }

    /// <summary><paramref name="cs"/> with <paramref name="application"/> as its application name.</summary>
    private static string Named(string cs, string application)
        => new SqlConnectionStringBuilder(cs) { ApplicationName = application }.ConnectionString;

    /// <summary>Waits until a request of the session named <paramref name="application"/> is blocked by
    /// <paramref name="blocker"/>, and returns that session.</summary>
    private static async Task<short> BlockedByAsync(string cs, string application, short blocker)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await IntegrationDb.ScalarAsync<short?>(cs,
                "SELECT TOP (1) CAST(r.[session_id] AS smallint) FROM sys.dm_exec_requests AS r " +
                "JOIN sys.dm_exec_sessions AS s ON s.[session_id] = r.[session_id] " +
                $"WHERE s.[program_name] = N'{application}' AND r.[blocking_session_id] = {blocker};");
            if (blocked is { } session)
            {
                return session;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"No request of '{application}' was blocked by session {blocker} within {Patience.TotalSeconds} seconds.");
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleParents.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.PipelineColumns.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    /// <summary>Writes a file flow whose <c>schedule:</c> is <paramref name="scheduleBlock"/>, written from column 0 (an
    /// empty block declares none).</summary>
    private void WriteFlow(string flowName, string scheduleBlock)
        => File.WriteAllText(Path.Combine(_dir, flowName + ".yaml"), $$"""
            name: {{flowName}}
            {{scheduleBlock}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: LockOrders
            """);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone
        }
    }
}

/// <summary>Runs <see cref="ScheduleNameLockIntegrationTests"/> alone, after the parallel collections: it interleaves a
/// sync and an API create by holding catalog locks, which nothing else may contend with while it does.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScheduleNameLockGroup
{
    public const string Name = "Schedule-name lock interleaving";
}
