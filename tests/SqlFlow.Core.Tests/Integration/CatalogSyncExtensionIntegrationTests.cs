using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A host module's document families in the repository sync (<see cref="ICatalogSyncExtension"/>) against the real
/// catalog: every registered extension runs inside the sync's reconciliation transaction, after the pipelines are
/// staged, with the repository's id and materialized root; the tallies of all extensions are summed onto the result and
/// their warnings reported; and an extension that throws, or returns no result, fails the sync and rolls every write
/// back, so the catalog never shows a repository half reconciled. An extension also decides whether an otherwise
/// unchanged estate recomputes its lineage, because a companion document it owns can change what a flow declares, and
/// does its work after the sync's transaction committed, every extension once all of them reconciled, with what it reports
/// joined to the result. Cleans up its own repository's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncExtensionIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catsyncext_" + Guid.NewGuid().ToString("N"));

    public CatalogSyncExtensionIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Extensions_RunInsideTheSyncTransaction_AndTheirTalliesAndWarningsAreReported()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, flowName) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);
        var first = new ObservingExtension(flowName, new CatalogSyncExtensionResult(1, 2, 3, 4, 5), "mappings/m.yaml: not a mapping.");
        var second = new ObservingExtension(flowName, new CatalogSyncExtensionResult(1, 0, 0, 0, 1), null);

        try
        {
            var before = DateTime.UtcNow;
            await using var db = CatalogDatabase.Create(cs);
            var result = await new CatalogSync(YamlDocumentLoader.CreateDefault(), [first, second])
                .SyncAsync(db, _dir, repo, null, before);

            Assert.Equal(1, result.PipelinesAdded);
            Assert.Equal((2, 2, 3, 4, 6), (result.DocumentsAdded, result.DocumentsUpdated, result.DocumentsUnchanged, result.DocumentsRemoved, result.DocumentsInvalid));
            Assert.Contains("mappings/m.yaml: not a mapping.", result.Warnings);
            foreach (var extension in new[] { first, second })
            {
                Assert.Equal((repoId, Path.GetFullPath(_dir), before), (extension.RepoId, extension.Root, extension.NowUtc));
                Assert.True(extension.InTransaction);
                Assert.True(extension.SawStagedPipeline);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    [SkippableFact]
    public async Task AnExtensionThatThrows_FailsTheSync_AndNothingIsCommitted()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, _) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CatalogSync(YamlDocumentLoader.CreateDefault(), [new ThrowingExtension()]).SyncAsync(db, _dir, repo, null, DateTime.UtcNow));
                Assert.Equal("the module's documents could not be reconciled.", failure.Message);
            }

            await AssertNothingCommittedAsync(cs, repoId);
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    [SkippableFact]
    public async Task AnExtensionThatReturnsNoResult_FailsTheSync_NamingIt_AndNothingIsCommitted()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, _) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CatalogSync(YamlDocumentLoader.CreateDefault(), [new SilentExtension()]).SyncAsync(db, _dir, repo, null, DateTime.UtcNow));
                Assert.Contains(nameof(SilentExtension), failure.Message, StringComparison.Ordinal);
            }

            await AssertNothingCommittedAsync(cs, repoId);
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    [SkippableFact]
    public async Task AnUnchangedEstate_RecomputesLineage_OnlyWhenAnExtensionReportsItsLineageInputsChanged()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, _) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);
        var gate = new LineageGateExtension();

        try
        {
            // A new estate recomputes on the flows' own account; the extension is not asked.
            var first = await SyncWithAsync(cs, repo, gate);
            Assert.True(first.LineageEdges > 0);
            Assert.Equal(0, gate.Asked);

            // Unchanged flows and an extension reporting nothing changed: the stored lineage is current.
            var unchanged = await SyncWithAsync(cs, repo, gate);
            Assert.Equal(0, unchanged.LineageEdges);
            Assert.Equal(1, gate.Asked);
            Assert.Equal((repoId, Path.GetFullPath(_dir)), (gate.RepoId, gate.Root));
            Assert.False(gate.InTransaction);

            // Unchanged flows but a changed companion document: the lineage is recomputed.
            gate.Changed = true;
            var recomputed = await SyncWithAsync(cs, repo, gate);
            Assert.Equal(first.LineageEdges, recomputed.LineageEdges);
            Assert.Equal(2, gate.Asked);
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    [SkippableFact]
    public async Task AnExtensionFailingToReportItsLineageInputs_FailsTheSync()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, _) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await SyncWithAsync(cs, repo, new LineageGateExtension());

            var failure = await Assert.ThrowsAsync<IOException>(() =>
                SyncWithAsync(cs, repo, new LineageGateExtension { Failure = new IOException("the mappings could not be read.") }));
            Assert.Equal("the mappings could not be read.", failure.Message);
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    [SkippableFact]
    public async Task CommittedWork_RunsOnceTheTransactionCommitted_AfterEveryExtensionReconciled_AndItsWarningsAreReported()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, flowName) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);
        var calls = new List<string>();
        var first = new CommittingExtension("first", cs, flowName, calls, "moved what the reconciliation recorded.");
        var second = new CommittingExtension("second", cs, flowName, calls, null);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var result = await new CatalogSync(YamlDocumentLoader.CreateDefault(), [first, second]).SyncAsync(db, _dir, repo, null, DateTime.UtcNow);

            Assert.Equal(["first:sync", "second:sync", "first:committed", "second:committed"], calls);
            Assert.Equal("moved what the reconciliation recorded.", Assert.Single(result.Warnings, w => w.StartsWith("moved", StringComparison.Ordinal)));
            foreach (var extension in new[] { first, second })
            {
                Assert.Equal(repoId, extension.CommittedRepoId);
                Assert.False(extension.CommittedInTransaction);
                Assert.True(extension.SawCommittedPipeline);
            }
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    [SkippableFact]
    public async Task CommittedWorkThatThrows_Escapes_WithTheSyncCommitted()
    {
        var cs = IntegrationDb.Require();
        var (repo, repoId, flowName) = NewEstate();
        await CatalogDatabase.MigrateAsync(cs);
        var failing = new CommittingExtension("failing", cs, flowName, [], null) { Failure = new InvalidOperationException("the moves could not be made.") };

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CatalogSync(YamlDocumentLoader.CreateDefault(), [failing]).SyncAsync(db, _dir, repo, null, DateTime.UtcNow));
                Assert.Equal("the moves could not be made.", failure.Message);
            }

            await using var check = CatalogDatabase.Create(cs);
            Assert.True(await check.Pipelines.AnyAsync(p => p.RepoId == repoId && p.Name == flowName));
        }
        finally
        {
            await CleanupAsync(cs, repoId, repo);
        }
    }

    private async Task<CatalogSyncResult> SyncWithAsync(string cs, string repo, ICatalogSyncExtension extension)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync(YamlDocumentLoader.CreateDefault(), [extension]).SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
    }

    private (string Repo, Guid RepoId, string FlowName) NewEstate()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_ext_" + suffix;
        var flowName = "cat_ext_orders_" + suffix;
        var path = Path.Combine(_dir, "flows", "orders.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The target table is this test's own, so the lineage objects the sync registers are too, and cleanup removes
        // exactly them.
        File.WriteAllText(path, $"""
            name: {flowName}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${"${env:SQLFlowSinkConStr}"}
              schema: dbo
              table: {TableFor(repo)}
            """);
        return (repo, FlowIdentity.FromName(repo), flowName);
    }

    private static string TableFor(string repo) => "CatExt_" + repo["cat_ext_".Length..];

    private static async Task AssertNothingCommittedAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        Assert.False(await db.Repos.AnyAsync(r => r.Id == repoId));
        Assert.False(await db.Pipelines.AnyAsync(p => p.RepoId == repoId));
        Assert.False(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId));
    }

    private static async Task CleanupAsync(string cs, Guid repoId, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        // The objects go before the edges: the catalog sync of any concurrently running test sweeps database-less objects
        // no edge references, so an object left without its edges, even briefly, would be counted by that other sync.
        var table = TableFor(repo).ToLowerInvariant();
        var objectKeys = await db.Objects.Where(o => o.Name == table).Select(o => o.Key).ToListAsync();
        await db.ObjectColumns.Where(c => objectKeys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
        await db.Objects.Where(o => objectKeys.Contains(o.Key)).ExecuteDeleteAsync();
        await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
        await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    /// <summary>Records what the sync handed it and whether it ran inside the sync's transaction with the pipelines
    /// already staged, then reports a fixed tally and an optional warning.</summary>
    private sealed class ObservingExtension(string flowName, CatalogSyncExtensionResult tally, string? warning) : ICatalogSyncExtension
    {
        public Guid RepoId { get; private set; }

        public string? Root { get; private set; }

        public DateTime NowUtc { get; private set; }

        public bool InTransaction { get; private set; }

        public bool SawStagedPipeline { get; private set; }

        public Task<CatalogSyncExtensionResult> SyncAsync(
            CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
        {
            RepoId = repoId;
            Root = root;
            NowUtc = nowUtc;
            InTransaction = context.Database.CurrentTransaction is not null;
            SawStagedPipeline = context.Pipelines.Local.Any(p => p.RepoId == repoId && p.Name == flowName);
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            return Task.FromResult(tally);
        }
    }

    /// <summary>
    /// Records the order it is called in, and whether its work after the commit runs outside any transaction and finds the
    /// sync's pipeline committed, read on a connection of its own; reports a warning from that work, or throws there.
    /// </summary>
    private sealed class CommittingExtension(string name, string cs, string flowName, List<string> calls, string? warning) : ICatalogSyncExtension
    {
        public Exception? Failure { get; init; }

        public Guid CommittedRepoId { get; private set; }

        public bool CommittedInTransaction { get; private set; }

        public bool SawCommittedPipeline { get; private set; }

        public Task<CatalogSyncExtensionResult> SyncAsync(
            CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
        {
            calls.Add(name + ":sync");
            return Task.FromResult(CatalogSyncExtensionResult.Empty);
        }

        public async Task CommittedAsync(CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
        {
            calls.Add(name + ":committed");
            CommittedRepoId = repoId;
            CommittedInTransaction = context.Database.CurrentTransaction is not null;
            await using (var other = CatalogDatabase.Create(cs))
            {
                SawCommittedPipeline = await other.Pipelines.AnyAsync(p => p.RepoId == repoId && p.Name == flowName, ct);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }
    }

    /// <summary>Reports a settable answer to whether its lineage inputs changed, counting how often it was asked and
    /// recording what it was handed; its reconciliation reconciles nothing.</summary>
    private sealed class LineageGateExtension : ICatalogSyncExtension
    {
        public bool Changed { get; set; }

        public Exception? Failure { get; init; }

        public int Asked { get; private set; }

        public Guid RepoId { get; private set; }

        public string? Root { get; private set; }

        public bool InTransaction { get; private set; }

        public Task<CatalogSyncExtensionResult> SyncAsync(
            CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
            => Task.FromResult(CatalogSyncExtensionResult.Empty);

        public Task<bool> LineageInputsChangedAsync(CatalogDbContext context, Guid repoId, string root, CancellationToken ct)
        {
            Asked++;
            RepoId = repoId;
            Root = root;
            InTransaction = context.Database.CurrentTransaction is not null;
            return Failure is null ? Task.FromResult(Changed) : Task.FromException<bool>(Failure);
        }
    }

    private sealed class ThrowingExtension : ICatalogSyncExtension
    {
        public Task<CatalogSyncExtensionResult> SyncAsync(
            CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
            => throw new InvalidOperationException("the module's documents could not be reconciled.");
    }

    private sealed class SilentExtension : ICatalogSyncExtension
    {
        public Task<CatalogSyncExtensionResult> SyncAsync(
            CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
            => Task.FromResult<CatalogSyncExtensionResult>(null!);
    }
}
