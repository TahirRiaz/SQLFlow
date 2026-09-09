using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Node;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The flow-version snapshot model: enqueueing a run copies the pipeline's exact YAML into the content-addressed
/// FlowVersion store and stamps the run with its hash, so the executing node loads the document from the catalog
/// instead of cloning the git remote (the fix for a schedule fanning out a whole batch storming Bitbucket with
/// authenticated clones). Covers the stamp, the content dedup (single and group enqueue), the deliberate
/// no-snapshot fallbacks (missing pipeline row, embedded literal credential), and the worker-side decision of
/// which documents can execute from a bare single-file snapshot at all. DB-backed cases are gated on a reachable
/// catalog database like the other suites; the repo-tree checks are pure parsing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FlowVersionSnapshotTests
{
    private const string SyncedSha = "0123456789abcdef0123456789abcdef01234567";

    [SkippableFact]
    public async Task Enqueue_SnapshotsThePipelineYaml_AndDedupsRepeatedEnqueues()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();
        var yaml = FlowYaml(name);
        var hash = CatalogProjection.Hash(yaml);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = await SeedRepoAsync(db, name);
            await SeedPipelineAsync(db, repoId, "flow-a", yaml, hash);

            var firstRunId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, "flow-a", "ing"), DateTime.UtcNow);
            var secondRunId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, "flow-a", "ing"), DateTime.UtcNow);

            var first = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == firstRunId);
            var second = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == secondRunId);
            Assert.Equal(hash, first.FlowVersionHash);
            Assert.Equal(hash, second.FlowVersionHash);

            // Content-addressed: two runs of one version share a single stored copy, holding the exact YAML.
            var versions = await db.FlowVersions.AsNoTracking().Where(v => v.ContentHash == hash).ToListAsync();
            var version = Assert.Single(versions);
            Assert.Equal(yaml, version.Yaml);
        }
        finally
        {
            await CleanupAsync(cs, name, hash);
        }
    }

    [SkippableFact]
    public async Task Enqueue_WithAnEmbeddedCredential_TakesNoSnapshot_AndKeepsTheGitPin()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();
        // The catalog stores this flow's YAML redacted, so a snapshot would not be the committed bytes: the run
        // must stay on the git materialization path (and keep its commit pin so that path works).
        var yaml = $"""
            flowType: ing
            name: {name}-flow-a
            connections:
              src: Server=s;Database=d;User ID=u;Password=hunter2
            """;
        var hash = CatalogProjection.Hash(yaml);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = await SeedRepoAsync(db, name);
            await SeedPipelineAsync(db, repoId, "flow-a", yaml, hash);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, "flow-a", "ing"), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Null(run.FlowVersionHash);
            Assert.Equal(SyncedSha, run.CommitSha);
            Assert.False(await db.FlowVersions.AsNoTracking().AnyAsync(v => v.ContentHash == hash));
        }
        finally
        {
            await CleanupAsync(cs, name, hash);
        }
    }

    [SkippableFact]
    public async Task Enqueue_PinnedToANonSyncedCommit_TakesNoSnapshot_SoTheGitPathRunsTheExactCommit()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();
        var yaml = FlowYaml(name);
        var hash = CatalogProjection.Hash(yaml);
        // A different commit than the repo's last synced one: the catalog snapshot holds the SYNCED bytes, not
        // this commit's, so stamping it would silently run the wrong version. The run must stay on the git path.
        const string otherSha = "abcabcabcabcabcabcabcabcabcabcabcabcabca";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = await SeedRepoAsync(db, name);
            await SeedPipelineAsync(db, repoId, "flow-a", yaml, hash);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, "flow-a", "ing", CommitSha: otherSha), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal(otherSha, run.CommitSha);
            Assert.Null(run.FlowVersionHash);
        }
        finally
        {
            await CleanupAsync(cs, name, hash);
        }
    }

    [SkippableFact]
    public async Task Enqueue_PinnedToTheSyncedCommit_StillSnapshots()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();
        var yaml = FlowYaml(name);
        var hash = CatalogProjection.Hash(yaml);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = await SeedRepoAsync(db, name);
            await SeedPipelineAsync(db, repoId, "flow-a", yaml, hash);

            // An explicit pin that equals the synced commit is the same content as the snapshot, so the DB path stands.
            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, "flow-a", "ing", CommitSha: SyncedSha), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal(SyncedSha, run.CommitSha);
            Assert.Equal(hash, run.FlowVersionHash);
        }
        finally
        {
            await CleanupAsync(cs, name, hash);
        }
    }

    [SkippableFact]
    public async Task Enqueue_WithoutAPipelineRow_TakesNoSnapshot()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = await SeedRepoAsync(db, name);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(repoId, "flow-a", "ing"), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Null(run.FlowVersionHash);
            Assert.Equal(SyncedSha, run.CommitSha); // the git fallback still identifies the exact version
        }
        finally
        {
            await CleanupAsync(cs, name);
        }
    }

    [SkippableFact]
    public async Task EnqueueGroup_StampsEveryMember_AndDedupsASharedVersionWithinOneTransaction()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();
        // Two members deliberately share identical YAML text (and so one content hash): the group enqueue stages
        // the version row once for both, exercising the in-transaction dedup, while the third member carries its
        // own content and its own row.
        var sharedYaml = FlowYaml(name);
        var sharedHash = CatalogProjection.Hash(sharedYaml);
        var ownYaml = FlowYaml(name + "-own");
        var ownHash = CatalogProjection.Hash(ownYaml);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = await SeedRepoAsync(db, name);
            await SeedPipelineAsync(db, repoId, "flow-a", sharedYaml, sharedHash);
            await SeedPipelineAsync(db, repoId, "flow-b", sharedYaml, sharedHash);
            await SeedPipelineAsync(db, repoId, "flow-c", ownYaml, ownHash);

            var result = await RunQueueStore.EnqueueGroupAsync(
                db,
                new RunGroupEnqueueRequest(repoId, RunGroupModes.Batch, "detail",
                [
                    new RunScopeMember("flow-a", "ing", 0, "detail"),
                    new RunScopeMember("flow-b", "ing", 0, "detail"),
                    new RunScopeMember("flow-c", "ing", 1, "detail"),
                ]),
                DateTime.UtcNow);

            var members = await db.Runs.AsNoTracking()
                .Where(r => result.RunIds.Contains(r.RunId))
                .OrderBy(r => r.FlowName)
                .Select(r => new { r.FlowName, r.FlowVersionHash })
                .ToListAsync();
            Assert.Equal(3, members.Count);
            Assert.Equal(sharedHash, members[0].FlowVersionHash);
            Assert.Equal(sharedHash, members[1].FlowVersionHash);
            Assert.Equal(ownHash, members[2].FlowVersionHash);

            Assert.Equal(1, await db.FlowVersions.AsNoTracking().CountAsync(v => v.ContentHash == sharedHash));
            Assert.Equal(1, await db.FlowVersions.AsNoTracking().CountAsync(v => v.ContentHash == ownHash));
        }
        finally
        {
            await CleanupAsync(cs, name, sharedHash, ownHash);
        }
    }

    // ---- The worker-side snapshot-executability decision (pure parsing, no database) -------------------------------

    private static readonly YamlDocumentLoader Loader = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

    [Fact]
    public void FileFlow_WithARelativeLocalLocation_RequiresTheRepoTree()
    {
        // ./data/orders.csv refers to a sibling committed alongside the flow file: only a git checkout has it.
        var doc = Loader.Parse("""
            name: orders
            source:
              type: csv
              location: ./data/orders.csv
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: dbo
              table: Orders
            """);
        Assert.True(RunWorker.RequiresRepoTree(doc));
    }

    [Fact]
    public void FileFlow_WithACloudLocation_ExecutesFromABareSnapshot()
    {
        var doc = Loader.Parse("""
            name: orders
            source:
              type: csv
              location: https://account.dfs.core.windows.net/lake/raw/orders/
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: dbo
              table: Orders
            """);
        Assert.False(RunWorker.RequiresRepoTree(doc));
    }

    [Fact]
    public void ExportFlow_WithARelativeTargetPath_RequiresTheRepoTree()
    {
        var doc = Loader.Parse("""
            flowType: exp
            name: orders-export
            connections:
              dwh: ${env:DWH}
            source:
              server: dwh
              object: DW.raw.Orders
            target:
              path: ./out
            """);
        Assert.True(RunWorker.RequiresRepoTree(doc));
    }

    [Fact]
    public void SourceControlFlow_WithARelativeRepositoryPath_RequiresTheRepoTree()
    {
        var doc = Loader.Parse("""
            flowType: scm
            name: warehouse-scm
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./scm/warehouse
            """);
        Assert.True(RunWorker.RequiresRepoTree(doc));
    }

    [Fact]
    public void IngestionFlow_ExecutesFromABareSnapshot()
    {
        // Relational ingestion addresses servers and objects, never sibling files: always snapshot-safe.
        var doc = Loader.Parse("""
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.dbo.Orders
            target:
              server: dwh
              object: dw.raw.Orders
            load:
              keyColumns: [OrderID]
            """);
        Assert.False(RunWorker.RequiresRepoTree(doc));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static string UniqueName() => $"snapshot-test-{Guid.NewGuid():N}";

    /// <summary>A minimal, reference-only (snapshot-eligible) ingestion flow whose text embeds the unique test
    /// name, so every test run hashes to fresh content and never collides with a concurrent suite.</summary>
    private static string FlowYaml(string name) => $$"""
        flowType: ing
        name: {{name}}-flow
        connections:
          src: ${env:SRC}
          dwh: ${env:DWH}
        source:
          server: src
          object: db.dbo.Orders
        target:
          server: dwh
          object: dw.raw.Orders
        load:
          keyColumns: [OrderID]
        """;

    private static Guid RepoId(string name) => SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");

    private static async Task<Guid> SeedRepoAsync(CatalogDbContext db, string name)
    {
        var repoId = RepoId(name);
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = name,
            RemoteUrl = "https://example.test/repo.git",
            RootPath = null,
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        db.RepoSources.Add(new CatalogRepoSource
        {
            Id = SqlFlow.Core.Identity.FlowIdentity.FromName($"reposource/{name}"),
            Name = name,
            RemoteUrl = "https://example.test/repo.git",
            Branch = "main",
            Enabled = true,
            SyncIntervalSeconds = 300,
            LastSyncedSha = SyncedSha,
            LastSyncUtc = now,
            CreatedUtc = now,
            UpdatedUtc = now,
        });
        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task SeedPipelineAsync(
        CatalogDbContext db, Guid repoId, string flowName, string yaml, string hash)
    {
        var now = DateTime.UtcNow;
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "ing",
            RelativePath = $"{flowName}.flow.yaml",
            ContentHash = hash,
            Yaml = yaml,
            Active = true,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string cs, string name, params string?[] hashes)
    {
        await using var db = CatalogDatabase.Create(cs);
        var repoId = RepoId(name);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.RepoSources.Where(s => s.Name == name).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        foreach (var hash in hashes)
        {
            if (!string.IsNullOrEmpty(hash))
            {
                await db.FlowVersions.Where(v => v.ContentHash == hash).ExecuteDeleteAsync();
            }
        }
    }
}
