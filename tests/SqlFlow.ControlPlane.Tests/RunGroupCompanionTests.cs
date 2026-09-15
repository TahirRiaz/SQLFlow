using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Identity;
using SqlFlow.Dispatch;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A run group enqueued together with a caller's own rows (<see cref="RunGroupCompanion"/>) over the real catalog
/// database: the companion runs inside the enqueue's serializable transaction once the members are written, sees them,
/// and its writes commit with them; a companion that declines, or throws, leaves neither the group nor its own writes
/// behind and the context clean; and through the control plane's dispatcher the members are placed only once the
/// transaction committed. The companion's rows live in a table of a test schema this class creates and drops.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunGroupCompanionTests
{
    [SkippableFact]
    public async Task ACompanionThatCommits_WritesBesideTheMembers_InTheSameTransaction()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        await EnsureNoteTableAsync(cs);

        try
        {
            var inTransaction = false;
            var membersSeen = -1;
            await using var db = CatalogDatabase.Create(cs);
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, Request(repoId, suffix), DateTime.UtcNow,
                async (catalog, planned, ct) =>
                {
                    inTransaction = catalog.Database.CurrentTransaction is not null;
                    membersSeen = await catalog.Runs.AsNoTracking().CountAsync(r => r.GroupId == planned.GroupId, ct);
                    await WriteNoteAsync(catalog, planned, ct);
                    return true;
                });

            Assert.NotNull(result);
            Assert.True(inTransaction);
            Assert.Equal(2, membersSeen);
            await using var check = CatalogDatabase.Create(cs);
            Assert.Equal(2, await check.Runs.CountAsync(r => r.GroupId == result.GroupId));
            Assert.True(await check.RunGroups.AnyAsync(g => g.GroupId == result.GroupId));
            Assert.Equal(1, await CountNotesAsync(check, result.GroupId));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ACompanionThatDeclines_LeavesNothingBehind()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        await EnsureNoteTableAsync(cs);

        try
        {
            var plannedGroup = Guid.Empty;
            await using var db = CatalogDatabase.Create(cs);
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, Request(repoId, suffix), DateTime.UtcNow,
                async (catalog, planned, ct) =>
                {
                    plannedGroup = planned.GroupId;
                    await WriteNoteAsync(catalog, planned, ct);
                    return false;
                });

            Assert.Null(result);
            Assert.NotEqual(Guid.Empty, plannedGroup);
            Assert.Empty(db.ChangeTracker.Entries());
            await AssertNothingCommittedAsync(cs, repoId, plannedGroup);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ACompanionThatThrows_RollsTheEnqueueBack_AndTheExceptionPropagates()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        await EnsureNoteTableAsync(cs);

        try
        {
            var plannedGroup = Guid.Empty;
            await using var db = CatalogDatabase.Create(cs);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunQueueStore.EnqueueGroupAsync(
                db, Request(repoId, suffix), DateTime.UtcNow,
                async (catalog, planned, ct) =>
                {
                    plannedGroup = planned.GroupId;
                    await WriteNoteAsync(catalog, planned, ct);
                    throw new InvalidOperationException("the caller's rows could not be written.");
                }));

            Assert.Equal("the caller's rows could not be written.", failure.Message);
            Assert.Empty(db.ChangeTracker.Entries());
            await AssertNothingCommittedAsync(cs, repoId, plannedGroup);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task TheDispatcher_PlacesTheMembersOnlyOnceTheCompanionCommitted()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var pool = "companion_" + suffix;
        await EnsureNoteTableAsync(cs);

        try
        {
            using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithSetting("ControlPlane:Worker:Enabled", "false");
            var runs = factory.Services.GetRequiredService<IRunDispatcher>();
            var dispatcher = factory.Services.GetRequiredService<Dispatcher>();
            await WaitUntilActiveAsync(dispatcher);

            await using (var db = CatalogDatabase.Create(cs))
            {
                var declined = await runs.EnqueueGroupAsync(
                    db, Request(repoId, suffix, pool), (_, _, _) => Task.FromResult(false));
                Assert.Null(declined);
                Assert.Equal(0, dispatcher.Queue.CountEligibleQueuedRuns(pool));

                var committed = await runs.EnqueueGroupAsync(
                    db, Request(repoId, suffix, pool),
                    async (catalog, planned, ct) =>
                    {
                        await WriteNoteAsync(catalog, planned, ct);
                        return true;
                    });
                Assert.NotNull(committed);
                // Only the first wave is eligible; the second waits for it.
                Assert.Equal(1, dispatcher.Queue.CountEligibleQueuedRuns(pool));
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    private static RunGroupEnqueueRequest Request(Guid repoId, string suffix, string? pool = null)
        => new(
            repoId, RunGroupModes.Node, $"a_{suffix}",
            [new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch), new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch)],
            TargetPool: pool);

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("companion_" + suffix), suffix);
    }

    private static Task WriteNoteAsync(CatalogDbContext catalog, RunGroupEnqueueResult planned, CancellationToken ct)
        => catalog.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [sqlflow_test_companion].[GroupNote] ([GroupId], [MemberCount]) VALUES ({planned.GroupId}, {planned.RunIds.Count})",
            ct);

    private static Task<int> CountNotesAsync(CatalogDbContext db, Guid groupId)
        => db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [sqlflow_test_companion].[GroupNote] WHERE [GroupId] = {groupId}")
            .SingleAsync();

    private static async Task AssertNothingCommittedAsync(string cs, Guid repoId, Guid plannedGroup)
    {
        await using var check = CatalogDatabase.Create(cs);
        Assert.False(await check.Runs.AnyAsync(r => r.RepoId == repoId));
        Assert.False(await check.RunGroups.AnyAsync(g => g.RepoId == repoId));
        Assert.Equal(0, await CountNotesAsync(check, plannedGroup));
    }

    private static async Task WaitUntilActiveAsync(Dispatcher dispatcher)
    {
        // A previous owner that died without releasing the lease makes the successor wait out its TTL.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (!dispatcher.IsActive)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the host's dispatcher did not take ownership within the timeout.");
            }

            await Task.Delay(100);
        }
    }

    private static async Task EnsureNoteTableAsync(string cs)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Database.ExecuteSqlRawAsync(
            "IF SCHEMA_ID('sqlflow_test_companion') IS NULL EXEC('CREATE SCHEMA [sqlflow_test_companion]');");
        await db.Database.ExecuteSqlRawAsync(
            "IF OBJECT_ID('[sqlflow_test_companion].[GroupNote]') IS NULL "
            + "CREATE TABLE [sqlflow_test_companion].[GroupNote] ([GroupId] uniqueidentifier NOT NULL PRIMARY KEY, [MemberCount] int NOT NULL);");
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync(
            "IF OBJECT_ID('[sqlflow_test_companion].[GroupNote]') IS NOT NULL DROP TABLE [sqlflow_test_companion].[GroupNote];");
        await db.Database.ExecuteSqlRawAsync(
            "IF SCHEMA_ID('sqlflow_test_companion') IS NOT NULL EXEC('DROP SCHEMA [sqlflow_test_companion]');");
    }
}
