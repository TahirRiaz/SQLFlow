using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using NodeKey = SqlFlow.Lineage.Collection.NodeKey;
using ServerIdentity = SqlFlow.Lineage.Collection.ServerIdentity;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The endpoint sweep of the repository sync against the real catalog: a file or dataset node exists only because a flow
/// declares it, so once the repository that named it stops naming it, and no other repository's edges reference it, the
/// sync removes the row. A node another repository still reads is kept, and so is a node this repository never named
/// (a row another sync is writing). Two repositories with the same layout name the same file node, which is what lets
/// the test share one. Cleans up every row it seeded or synced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncEndpointSweepIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catsweep_" + Guid.NewGuid().ToString("N"));

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Estate(string repo) => Path.Combine(_dir, repo);

    private string Table(string repo) => $"Sweep_{repo}_{_suffix}";

    /// <summary>A file flow in the repository's <c>flows</c> folder reading <paramref name="folder"/> next to it.</summary>
    private void WriteFileFlow(string repo, string folder)
    {
        var path = Path.Combine(Estate(repo), "flows", "load.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            name: sweep_{repo}_{_suffix}
            source:
              type: csv
              location: ./{Folder(folder)}
            target:
              connection: {"${env:SQLFlowSinkConStr}"}
              schema: dbo
              table: {Table(repo)}
            """);
    }

    /// <summary>A registered flow in the repository writing the dataset <paramref name="name"/>.</summary>
    private void WriteDatasetFlow(string repo, string name)
    {
        var path = Path.Combine(Estate(repo), "flows", "probe.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            flowType: probe
            name: sweep_probe_{repo}_{_suffix}
            source: s3://drops/probe/
            datasets:
              - {"{"} relation: writes, system: probe-store, namespace: "t_{_suffix}", group: g, name: "{name}" {"}"}
            """);
    }

    private string FileKey(string folder) => NodeKey.For(ServerIdentity.FileSystem, null, null, $"flows/{Folder(folder)}");

    /// <summary>A folder name of this test's own, so no other test's estate names the same node.</summary>
    private string Folder(string folder) => $"{folder}_{_suffix}";

    private string DatasetKey(string name) => NodeKey.For(ServerIdentity.Dataset("probe-store", null), $"t_{_suffix}", "g", name);

    private async Task<CatalogSyncResult> SyncAsync(string cs, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync(YamlDocumentLoader.CreateDefault([new ProbeFlowKind()]), [])
            .SyncAsync(db, Estate(repo), RepoName(repo), null, DateTime.UtcNow);
    }

    private string RepoName(string repo) => $"cat_sweep_{repo}_{_suffix}";

    private static async Task<bool> ExistsAsync(string cs, string key)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.Objects.AnyAsync(o => o.Key == key);
    }

    [SkippableFact]
    public async Task A_file_node_goes_once_no_repository_names_it_and_a_node_this_repository_never_named_stays()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var orphan = NodeKey.For(ServerIdentity.FileSystem, null, null, $"seeded_{_suffix}/in");
        var now = DateTime.UtcNow;

        try
        {
            // A row with no edge that neither repository ever named: another sync may be about to reference it.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(new CatalogObject
                {
                    Key = orphan, ServerRef = ServerIdentity.FileSystem, Name = $"seeded_{_suffix}/in", Kind = "File",
                    FirstSeenUtc = now, LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            WriteFileFlow("a", "in");
            WriteFileFlow("b", "in");
            await SyncAsync(cs, "a");
            await SyncAsync(cs, "b");
            Assert.True(await ExistsAsync(cs, FileKey("in")));

            // Repository a moves on; b still reads the folder, so its node stays.
            WriteFileFlow("a", "moved");
            await SyncAsync(cs, "a");
            Assert.True(await ExistsAsync(cs, FileKey("in")));
            Assert.True(await ExistsAsync(cs, FileKey("moved")));

            // Repository b moves on too: nothing names the folder any more.
            WriteFileFlow("b", "moved");
            var swept = await SyncAsync(cs, "b");
            Assert.True(swept.ObjectsSuperseded >= 1);
            Assert.False(await ExistsAsync(cs, FileKey("in")));
            Assert.True(await ExistsAsync(cs, FileKey("moved")));
            Assert.True(await ExistsAsync(cs, orphan));
        }
        finally
        {
            await CleanupAsync(cs, [orphan, FileKey("in"), FileKey("moved")]);
        }
    }

    [SkippableFact]
    public async Task A_dataset_node_goes_once_its_flow_writes_another()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            WriteDatasetFlow("c", "first");
            await SyncAsync(cs, "c");
            Assert.True(await ExistsAsync(cs, DatasetKey("first")));

            WriteDatasetFlow("c", "second");
            var swept = await SyncAsync(cs, "c");

            Assert.Equal(1, swept.ObjectsSuperseded);
            Assert.False(await ExistsAsync(cs, DatasetKey("first")));
            Assert.True(await ExistsAsync(cs, DatasetKey("second")));
        }
        finally
        {
            await CleanupAsync(cs, [DatasetKey("first"), DatasetKey("second")]);
        }
    }

    private async Task CleanupAsync(string cs, IReadOnlyList<string> keys)
    {
        await using var db = CatalogDatabase.Create(cs);
        var repoIds = new[] { "a", "b", "c" }.Select(r => FlowIdentity.FromName(RepoName(r))).ToList();
        var tables = new[] { "a", "b" }.Select(r => Table(r).ToLowerInvariant()).ToList();
        var tableKeys = await db.Objects.Where(o => tables.Contains(o.Name)).Select(o => o.Key).ToListAsync();
        var all = keys.Concat(tableKeys).ToList();
        await db.ObjectColumns.Where(c => all.Contains(c.ObjectKey)).ExecuteDeleteAsync();
        await db.Objects.Where(o => all.Contains(o.Key)).ExecuteDeleteAsync();
        await db.FlowDependencies.Where(d => repoIds.Contains(d.RepoId)).ExecuteDeleteAsync();
        await db.LineageEdges.Where(e => repoIds.Contains(e.RepoId)).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId != null && repoIds.Contains(r.RepoId.Value)).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => repoIds.Contains(p.RepoId)).ExecuteDeleteAsync();
        await db.Repos.Where(r => repoIds.Contains(r.Id)).ExecuteDeleteAsync();
    }
}
