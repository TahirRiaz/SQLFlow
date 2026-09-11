using SqlFlow.Catalog;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The location grouping the offline object-body enrichment uses to map an engine-generated two-part CREATE
/// (`[schema].[name]`) back to the report objects it belongs to. The crux is that a database-qualified object and
/// its database-less "unresolved" twin are the SAME physical table and must both be enriched, while the same
/// schema.name under two genuinely different databases is unresolvable from a two-part name and is dropped.
/// </summary>
public sealed class EnrichableLocationsTests
{
    private const string Server = "${env:CONN}";

    private static LineageObjectNode Node(string? database, string schema, string name, LineageNodeKind kind = LineageNodeKind.Table)
        => new()
        {
            Key = NodeKey.For(Server, database, schema, name),
            ServerRef = Server,
            Database = database,
            Schema = schema,
            Name = name,
            Kind = kind,
        };

    [Fact]
    public void QualifiedAndDatabaselessTwin_ShareOneLocation_AndBothAreEnrichable()
    {
        var qualified = Node("dw-pre-prod", "pre", "Boatbooking_detail");
        var twin = Node(null, "pre", "Boatbooking_detail");

        var map = CatalogSync.EnrichableLocations([qualified, twin]);

        var entry = Assert.Single(map);
        Assert.Equal(NodeKey.For(Server, null, "pre", "Boatbooking_detail"), entry.Key);
        Assert.Equal(2, entry.Value.Count);
        Assert.Contains(qualified.Key, entry.Value);
        Assert.Contains(twin.Key, entry.Value);
    }

    [Fact]
    public void SameSchemaName_UnderTwoDifferentDatabases_IsDroppedAsAmbiguous()
    {
        var inDbA = Node("db_a", "dbo", "Orders");
        var inDbB = Node("db_b", "dbo", "Orders");

        var map = CatalogSync.EnrichableLocations([inDbA, inDbB]);

        Assert.Empty(map);
    }

    [Fact]
    public void DistinctObjects_EachGetTheirOwnLocation()
    {
        var a = Node("dw", "pre", "A");
        var b = Node("dw", "pre", "B");

        var map = CatalogSync.EnrichableLocations([a, b]);

        Assert.Equal(2, map.Count);
        Assert.Equal(a.Key, Assert.Single(map[NodeKey.For(Server, null, "pre", "A")]));
        Assert.Equal(b.Key, Assert.Single(map[NodeKey.For(Server, null, "pre", "B")]));
    }

    [Fact]
    public void FileObjects_AreExcluded()
    {
        var file = Node(null, "", "az://acct/raw/orders", LineageNodeKind.File);
        var table = Node("dw", "pre", "Orders");

        var map = CatalogSync.EnrichableLocations([file, table]);

        Assert.Single(map);
        Assert.True(map.ContainsKey(NodeKey.For(Server, null, "pre", "Orders")));
    }
}
