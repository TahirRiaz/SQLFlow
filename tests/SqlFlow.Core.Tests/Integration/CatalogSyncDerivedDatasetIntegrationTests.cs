using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using NodeKey = SqlFlow.Lineage.Collection.NodeKey;
using ServerIdentity = SqlFlow.Lineage.Collection.ServerIdentity;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A derived dataset through the repository sync against the real catalog. The sync stores the dataset's own reads of
/// its sources with no pipeline, the flow's read of the dataset, and what the flow inherits through it, and levels the
/// dataset after its sources and before what the flow writes. What a flow inherits through a derived dataset is known
/// from the documents alone, so a sync that reaches no server still lets go of a source the documents no longer name:
/// its inherited edge is not kept as live-catalog knowledge would be, and its node is swept. Cleans up every row it
/// synced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncDerivedDatasetIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catderived_" + Guid.NewGuid().ToString("N"));

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string RepoName => $"cat_derived_{_suffix}";

    private string Namespace => $"t_{_suffix}";

    private string Writer => $"derived_writer_{_suffix}";

    private string Reader => $"derived_reader_{_suffix}";

    private string Stored(string relation, string name)
        => $"{{ relation: {relation}, system: probe-store, namespace: \"{Namespace}\", group: g, name: \"{name}\" }}";

    private string StoredKey(string name) => NodeKey.For(ServerIdentity.Dataset("probe-store", null), Namespace, "g", name);

    private string ModelKey => NodeKey.For(ServerIdentity.Dataset("probe-model", null), Namespace, "models", "model");

    private void Write(string file, params string[] lines)
    {
        var path = Path.Combine(_dir, "flows", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
    }

    /// <summary>The flow writing the sources the estate produces itself.</summary>
    private void WriteWriter()
        => Write(
            "writer.yaml",
            "flowType: probe", $"name: {Writer}", "source: s3://drops/probe/",
            "datasets:", "  - " + Stored("writes", "unit"), "  - " + Stored("writes", "code"));

    /// <summary>The flow reading the derived dataset, which is derived from <paramref name="sources"/>, and writing one of its own.</summary>
    private void WriteReader(params string[] sources)
        => Write(
            "reader.yaml",
            [
                "flowType: probe", $"name: {Reader}", "source: s3://drops/probe/",
                "datasets:", "  - " + Stored("writes", "out"),
                "derivations:",
                $"  - dataset: {{ relation: reads, system: probe-model, namespace: \"{Namespace}\", group: models, name: model }}",
                "    from:",
                .. sources.Select(source => "      - " + Stored("reads", source)),
            ]);

    private async Task<CatalogSyncResult> SyncAsync(string cs)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync(YamlDocumentLoader.CreateDefault([new ProbeFlowKind()]), [])
            .SyncAsync(db, _dir, RepoName, null, DateTime.UtcNow);
    }

    private async Task<List<(string? Flow, string? ViaModule, string Relation, string ObjectKey, string Tier)>> EdgesAsync(string cs)
    {
        await using var db = CatalogDatabase.Create(cs);
        var repoId = FlowIdentity.FromName(RepoName);
        var rows = await db.LineageEdges.AsNoTracking().Where(e => e.RepoId == repoId)
            .Select(e => new { e.Flow, e.ViaModule, e.Relation, e.ObjectKey, e.Tier })
            .ToListAsync();
        return rows.Select(e => (e.Flow, e.ViaModule, e.Relation, e.ObjectKey, e.Tier)).ToList();
    }

    private static async Task<int?> LevelAsync(string cs, string key)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.Objects.AsNoTracking().Where(o => o.Key == key).Select(o => o.Level).SingleAsync();
    }

    private static async Task<bool> ExistsAsync(string cs, string key)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await db.Objects.AnyAsync(o => o.Key == key);
    }

    [SkippableFact]
    public async Task A_derived_dataset_is_stored_between_its_sources_and_its_flow_and_lets_go_of_a_source_the_documents_drop()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            WriteWriter();
            WriteReader("unit", "extra");
            await SyncAsync(cs);

            var edges = await EdgesAsync(cs);
            Assert.Contains((null, ModelKey, "Reads", StoredKey("unit"), "Declared"), edges);
            Assert.Contains((null, ModelKey, "Reads", StoredKey("extra"), "Declared"), edges);
            Assert.Contains((Reader, null, "Reads", ModelKey, "Declared"), edges);
            Assert.Contains((Reader, ModelKey, "Reads", StoredKey("unit"), "Derived"), edges);
            Assert.Contains((Reader, ModelKey, "Reads", StoredKey("extra"), "Derived"), edges);
            Assert.DoesNotContain(edges, e => e.Flow == Reader && e.ViaModule is null && e.ObjectKey == StoredKey("unit"));

            // The reader runs after the flow writing a source of the dataset it reads.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var repoId = FlowIdentity.FromName(RepoName);
                var waves = await db.Pipelines.AsNoTracking().Where(p => p.RepoId == repoId).ToDictionaryAsync(p => p.Name, p => p.Wave);
                Assert.True(waves[Writer] < waves[Reader], $"writer wave {waves[Writer]}, reader wave {waves[Reader]}");
            }

            // Sources, then the dataset derived from them, then what the flow reading it writes.
            Assert.Equal(0, await LevelAsync(cs, StoredKey("unit")));
            Assert.Equal(0, await LevelAsync(cs, StoredKey("extra")));
            Assert.Equal(1, await LevelAsync(cs, ModelKey));
            Assert.Equal(2, await LevelAsync(cs, StoredKey("out")));

            // The dataset is derived from another of the writer's datasets instead: nothing names 'extra' any more, and
            // this sync, which reached no server, still lets go of what the flow inherited of it.
            WriteReader("unit", "code");
            var swept = await SyncAsync(cs);

            edges = await EdgesAsync(cs);
            Assert.DoesNotContain(edges, e => e.ObjectKey == StoredKey("extra"));
            Assert.Contains((null, ModelKey, "Reads", StoredKey("code"), "Declared"), edges);
            Assert.Contains((Reader, ModelKey, "Reads", StoredKey("code"), "Derived"), edges);
            Assert.Contains((Reader, ModelKey, "Reads", StoredKey("unit"), "Derived"), edges);
            Assert.Equal(1, swept.ObjectsSuperseded);
            Assert.False(await ExistsAsync(cs, StoredKey("extra")));
            Assert.True(await ExistsAsync(cs, ModelKey));

            // The flow stops reading the dataset: its node and everything inherited through it go with the read.
            Write("reader.yaml", "flowType: probe", $"name: {Reader}", "source: s3://drops/probe/", "datasets:", "  - " + Stored("writes", "out"));
            await SyncAsync(cs);

            edges = await EdgesAsync(cs);
            Assert.DoesNotContain(edges, e => e.ViaModule == ModelKey || e.ObjectKey == ModelKey);
            Assert.False(await ExistsAsync(cs, ModelKey));
            Assert.True(await ExistsAsync(cs, StoredKey("unit")));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var repoId = FlowIdentity.FromName(RepoName);
            var keys = new[] { "unit", "code", "extra", "out" }.Select(StoredKey).Append(ModelKey).ToList();
            await db.ObjectColumns.Where(c => keys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => keys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }
}
