using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The catalog forgets the database objects nothing knows any more, against the real sink: after a sync commits, an object
/// goes when nothing references it and either its server reference is retired (no flow, pipeline, subscriber query or edge
/// names it, and it was last seen past the grace period) or the pass read its server's database in full without finding it.
/// What is still referenced, curated by a person, or seen within the grace period stays. A connected pass leaves the
/// platform's own catalog tables out of what it lists. Every seeded row lives under server references unique to the test,
/// and is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogObjectForgettingIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catforget_" + Guid.NewGuid().ToString("N"));

    public CatalogObjectForgettingIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void WriteFlow(string flowName, string connection, string table)
    {
        var path = Path.Combine(_dir, "flows", flowName + ".flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: {{connection}}
              schema: dbo
              table: {{table}}
            """);
    }

    private static CatalogObject Seed(string serverRef, string database, string name, DateTime lastSeenUtc) => new()
    {
        Key = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, database, "dbo", name),
        ServerRef = serverRef,
        Database = database,
        Schema = "dbo",
        Name = name,
        Kind = "Table",
        FirstSeenUtc = lastSeenUtc,
        LastSeenUtc = lastSeenUtc,
    };

    [SkippableFact]
    public async Task Sync_ForgetsTheObjectsOfARetiredReference_AndKeepsWhatIsStillReferencedOrRecent()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var upper = suffix.ToUpperInvariant();
        var repo = "cat_forget_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var otherRepoId = FlowIdentity.FromName("cat_forget_other_" + suffix);
        var retired = "${env:SQLFLOW_TEST_RETIRED_" + upper + "}";
        var recent = "${env:SQLFLOW_TEST_RECENT_" + upper + "}";
        var live = "${env:SQLFLOW_TEST_LIVE_" + upper + "}";
        var longAgo = DateTime.UtcNow.AddDays(-2);

        var gone = Seed(retired, "OldDb", "Gone", longAgo);
        var related = Seed(retired, "OldDb", "Related", longAgo);
        var curated = Seed(retired, "OldDb", "Curated", longAgo);
        var fresh = Seed(recent, "NewDb", "Fresh", DateTime.UtcNow.AddMinutes(-5));
        var edged = Seed(live, "LiveDb", "Edged", longAgo);
        var sibling = Seed(live, "LiveDb", "Sibling", longAgo);
        CatalogObject[] seeded = [gone, related, curated, fresh, edged, sibling];
        var keys = seeded.Select(o => o.Key).ToList();

        WriteFlow("cat_forget_orders_" + suffix, "${env:SQLFlowSinkConStr}", "ForgetOrders_" + suffix);
        await CatalogDatabase.MigrateAsync(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.AddRange(seeded);
                db.Repos.Add(new CatalogRepo { Id = otherRepoId, Name = "cat_forget_other_" + suffix, FirstSeenUtc = longAgo, LastSyncUtc = longAgo });
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = otherRepoId, Flow = "other_" + suffix, Relation = "Reads", ObjectKey = edged.Key, ObjectName = edged.Name, Tier = "Declared",
                });
                db.ObjectRelationships.Add(new CatalogObjectRelationship
                {
                    RepoId = otherRepoId, FromObjectKey = related.Key, FromColumns = "Id", ToObjectKey = SqlFlow.Lineage.Collection.NodeKey.For(retired, "OldDb", "dbo", "Elsewhere"),
                    ToColumns = "Id", Origin = "Join", Tier = "Observed", Occurrences = 1,
                });
                db.ColumnPolicies.Add(new CatalogColumnPolicy
                {
                    ObjectKey = curated.Key, ColumnName = "Id", IsAllowed = true, UpdatedBy = "test", UpdatedUtc = longAgo,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            // What is left, not how many this sync removed: forgetting is catalog-wide, so a sync another test runs
            // meanwhile may forget the retired object first, and this sync's count is then zero.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var left = await db.Objects.Where(o => keys.Contains(o.Key)).Select(o => o.Name).ToListAsync();
                Assert.Equal(["Curated", "Edged", "Fresh", "Related", "Sibling"], left.Order(StringComparer.Ordinal));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ColumnPolicies.Where(p => keys.Contains(p.ObjectKey)).ExecuteDeleteAsync();
            await db.ObjectRelationships.Where(r => r.RepoId == otherRepoId || r.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == otherRepoId || e.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId || r.Id == otherRepoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => keys.Contains(o.Key) || o.Name == "ForgetOrders_" + suffix).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task ConnectedSync_LeavesOutTheCatalogsOwnTables_AndForgetsWhatItsDatabaseNoLongerHolds()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var variable = "SQLFLOW_TEST_READ_LIVE_" + suffix.ToUpperInvariant();
        var serverRef = "${env:" + variable + "}";
        var prefix = serverRef.ToLowerInvariant() + "|";
        var repo = "cat_forget_live_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var database = new SqlConnectionStringBuilder(cs).InitialCatalog;
        var dropped = Seed(serverRef, database, "Dropped_" + suffix, DateTime.UtcNow.AddDays(-2));

        WriteFlow("cat_forget_live_orders_" + suffix, serverRef, "ForgetLive_" + suffix);
        Environment.SetEnvironmentVariable(variable, cs);
        await CatalogDatabase.MigrateAsync(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(dropped);
                await db.SaveChangesAsync();
            }

            CatalogSyncResult result;
            await using (var db = CatalogDatabase.Create(cs))
            {
                result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, includeDerived: true);
            }

            Assert.True(result.LineageConnected);
            await using (var db = CatalogDatabase.Create(cs))
            {
                var listed = await db.Objects.Where(o => o.ServerRef == serverRef).Select(o => new { o.Key, o.Schema, o.Database }).ToListAsync();

                // The live read listed the database (the flow's target at least), named as the database names itself.
                var target = Assert.Single(listed, o => o.Key == SqlFlow.Lineage.Collection.NodeKey.For(serverRef, database, "dbo", "ForgetLive_" + suffix));
                Assert.Equal(database, target.Database, ignoreCase: true);

                // The catalog's own tables are the platform's, not the estate's.
                Assert.DoesNotContain(listed, o => string.Equals(o.Schema, CatalogDbContext.SchemaName, StringComparison.OrdinalIgnoreCase));

                // What the database no longer holds, and nothing references, is forgotten.
                Assert.DoesNotContain(listed, o => o.Key == dropped.Key);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.ObjectRelationships.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey.StartsWith(prefix)).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
        }
    }
}
