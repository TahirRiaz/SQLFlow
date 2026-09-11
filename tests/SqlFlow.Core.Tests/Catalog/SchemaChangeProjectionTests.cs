using System.Text.Json;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The schema-history projection: a source-control run's added/changed/deleted path lists mapped to the rows the
/// change feed reads. The object's identity comes from the snapshot path alone, so the parsing rules (schema-less
/// objects, dotted names, foreign layouts) are what this pins, along with the rule that a dry run records nothing.
/// </summary>
public sealed class SchemaChangeProjectionTests
{
    private static readonly Guid Repo = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Run = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Pipeline = new("22222222-2222-2222-2222-222222222222");

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string Snapshot = """
        {
          "schemaVersion": 1,
          "flowKind": "scm",
          "flowName": "dbsource_dwh_00_scm",
          "runId": "11111111-1111-1111-1111-111111111111",
          "success": true,
          "writtenUtc": "2026-08-23T03:30:00Z",
          "result": {
            "dryRun": false,
            "commitSha": "abcdef1234567890",
            "addedObjects": [ "dw-dwh-prod/Table/arc.Cyclehire_Bikes.sql" ],
            "changedObjects": [ "dw-dwh-prod/View/pre.v_Citybikes_Trips.sql", "dw-dwh-prod/Schema/arc.sql" ],
            "deletedObjects": [ "dw-dwh-prod/StoredProcedure/dbo.usp_Old.sql" ]
          }
        }
        """;

    [Fact]
    public void Projects_EveryChange_WithItsObjectIdentity()
    {
        var changes = CatalogProjection.SchemaChanges(Json(Snapshot), Run, Repo, Pipeline);

        Assert.Equal(4, changes.Count);
        Assert.All(changes, c =>
        {
            Assert.Equal(Repo, c.RepoId);
            Assert.Equal(Run, c.RunId);
            Assert.Equal(Pipeline, c.PipelineId);
            Assert.Equal("dw-dwh-prod", c.Database);
            Assert.Equal("abcdef1234567890", c.CommitSha);
            Assert.Equal(new DateTime(2026, 8, 23, 3, 30, 0, DateTimeKind.Utc), c.OccurredUtc);
        });

        var added = Assert.Single(changes, c => c.ChangeType == SchemaChangeKinds.Added);
        Assert.Equal("Table", added.Category);
        Assert.Equal("arc", added.Schema);
        Assert.Equal("Cyclehire_Bikes", added.Name);

        var dropped = Assert.Single(changes, c => c.ChangeType == SchemaChangeKinds.Deleted);
        Assert.Equal("StoredProcedure", dropped.Category);
        Assert.Equal("dbo", dropped.Schema);
        Assert.Equal("usp_Old", dropped.Name);

        Assert.Equal(2, changes.Count(c => c.ChangeType == SchemaChangeKinds.Changed));
    }

    [Fact]
    public void DryRun_RecordsNothing()
    {
        // A dry run writes a working tree nobody keeps. Recording it would date the change to a rehearsal and then
        // report the same change again when the real snapshot runs.
        var changes = CatalogProjection.SchemaChanges(
            Json(Snapshot.Replace("\"dryRun\": false", "\"dryRun\": true", StringComparison.Ordinal)),
            Run, Repo, Pipeline);

        Assert.Empty(changes);
    }

    [Fact]
    public void SchemaLessObject_KeepsItsBareName()
    {
        // A database DDL trigger has no schema, so the snapshot writes just <name>.sql and the row must carry a
        // null schema rather than inventing one out of the first dot.
        var changes = CatalogProjection.SchemaChanges(Json("""
            {
              "flowKind": "scm", "flowName": "s", "runId": "11111111-1111-1111-1111-111111111111",
              "success": true, "writtenUtc": "2026-08-23T03:30:00Z",
              "result": { "addedObjects": [ "dw-pre-prod/DatabaseDdlTrigger/trg_Audit.sql" ] }
            }
            """), Run, Repo, Pipeline);

        var change = Assert.Single(changes);
        Assert.Null(change.Schema);
        Assert.Equal("trg_Audit", change.Name);
        Assert.Equal("DatabaseDdlTrigger", change.Category);
    }

    [Fact]
    public void DottedObjectName_SplitsOnlyOnTheFirstDot()
    {
        var changes = CatalogProjection.SchemaChanges(Json("""
            {
              "flowKind": "scm", "flowName": "s", "runId": "11111111-1111-1111-1111-111111111111",
              "success": true, "writtenUtc": "2026-08-23T03:30:00Z",
              "result": { "changedObjects": [ "dw-pre-prod/View/pre.v_Fact.Sales.sql" ] }
            }
            """), Run, Repo, Pipeline);

        var change = Assert.Single(changes);
        Assert.Equal("pre", change.Schema);
        Assert.Equal("v_Fact.Sales", change.Name);
    }

    [Fact]
    public void ForeignPaths_AreSkipped_NotHalfIdentified()
    {
        var changes = CatalogProjection.SchemaChanges(Json("""
            {
              "flowKind": "scm", "flowName": "s", "runId": "11111111-1111-1111-1111-111111111111",
              "success": true, "writtenUtc": "2026-08-23T03:30:00Z",
              "result": { "addedObjects": [ "Readme.md", "db/Table/.sql", "a/b/c/d.sql", "" ] }
            }
            """), Run, Repo, Pipeline);

        Assert.Empty(changes);
    }

    [Theory]
    [InlineData("dw-dwh-prod/Table/arc.Cyclehire_Bikes.sql")]
    [InlineData("dw-dwh-prod/Schema/arc.sql")]
    [InlineData("dw-pre-prod/View/pre.APC_Dalane_Calls.sql")]
    [InlineData("dw-dwh-prod/Table/dbo.Order.Detail.sql")]
    public void SnapshotPath_RebuildsTheFileARowWasParsedFrom(string path)
    {
        // The schema drill-down reads an object's DDL back out of the repository by rebuilding its path from the
        // row, so the join has to be the exact inverse of the split. A dotted object name (only the FIRST dot
        // separates the schema) is the case that would silently address the wrong file if the two ever diverged.
        var changes = CatalogProjection.SchemaChanges(Json($$"""
            {
              "flowKind": "scm", "flowName": "s", "runId": "11111111-1111-1111-1111-111111111111",
              "success": true, "writtenUtc": "2026-08-23T03:30:00Z",
              "result": { "addedObjects": [ "{{path}}" ] }
            }
            """), Run, Repo, Pipeline);

        var change = Assert.Single(changes);
        Assert.Equal(path, CatalogProjection.SnapshotPath(change.Database, change.Category, change.Schema, change.Name));
    }

    [Fact]
    public void NonSourceControlRun_ProjectsNothing()
    {
        var changes = CatalogProjection.SchemaChanges(Json("""
            {
              "flowKind": "ing", "flowName": "load-orders", "runId": "11111111-1111-1111-1111-111111111111",
              "success": true, "writtenUtc": "2026-08-23T03:30:00Z",
              "result": { "rowsLoaded": 42 }
            }
            """), Run, Repo, Pipeline);

        Assert.Empty(changes);
    }
}
