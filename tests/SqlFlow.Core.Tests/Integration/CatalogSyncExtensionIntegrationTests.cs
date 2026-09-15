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
/// back, so the catalog never shows a repository half reconciled. Cleans up its own repository's rows.
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
            await CleanupAsync(cs, repoId);
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
            await CleanupAsync(cs, repoId);
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
            await CleanupAsync(cs, repoId);
        }
    }

    private (string Repo, Guid RepoId, string FlowName) NewEstate()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_ext_" + suffix;
        var flowName = "cat_ext_orders_" + suffix;
        var path = Path.Combine(_dir, "flows", "orders.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            name: {flowName}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${"${env:SQLFlowSinkConStr}"}
              schema: dbo
              table: CatExtOrders
            """);
        return (repo, FlowIdentity.FromName(repo), flowName);
    }

    private static async Task AssertNothingCommittedAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        Assert.False(await db.Repos.AnyAsync(r => r.Id == repoId));
        Assert.False(await db.Pipelines.AnyAsync(p => p.RepoId == repoId));
        Assert.False(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId));
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
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
