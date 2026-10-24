using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// How the graph names the objects the catalog lists: a node key folds case, so it decides which database and schema an
/// object is in, never how they are spelled; the platform's own tables a live inventory found are kept out of the listing
/// but still describe an object a flow reads; and the report names every server whose inventory it read in full, its
/// aliases with it. Pure in-memory: every fact is constructed directly, nothing touches a database, the clock, or disk.
/// </summary>
public sealed class LineageCatalogIdentityTests
{
    private static readonly DateTime GeneratedAt = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);

    private static CollectedFlow Flow(string name) => new()
    {
        Node = new LineageFlowNode { Name = name, Kind = "ing", File = name + ".flow.yaml", Batch = null },
        TargetServerRef = "@dwh",
        FileWriteUtc = GeneratedAt,
    };

    private static LineageFact Fact(string flow, LineageRelation relation, string? database, string schema, string name) => new()
    {
        Flow = flow,
        Relation = relation,
        ServerRef = "@dwh",
        Database = database,
        Schema = schema,
        Name = name,
        Tier = LineageTier.Declared,
    };

    private static LineageReport Build(CollectionResult collected)
        => LineageGraphBuilder.Build(collected, "flows", [LineageTier.Declared], GeneratedAt);

    [Fact]
    public void A_node_keeps_the_spelling_its_fact_gave_its_database_and_schema_when_no_inventory_describes_it()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("report"));
        collected.Facts.Add(Fact("report", LineageRelation.Reads, "Warehouse", "Sales", "v_SalesOrders"));

        var node = Assert.Single(Build(collected).Objects);

        Assert.Equal("@dwh|warehouse|sales|v_salesorders", node.Key);
        Assert.Equal("Warehouse", node.Database);
        Assert.Equal("Sales", node.Schema);
        Assert.Equal("v_SalesOrders", node.Name);
    }

    [Fact]
    public void A_node_on_its_servers_default_database_takes_the_connections_own_spelling_of_it()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("report"));
        collected.ServerDefaultDatabases["@dwh"] = "WAREHOUSE";
        collected.Facts.Add(Fact("report", LineageRelation.Reads, "warehouse", "sales", "v_SalesOrders"));
        collected.Facts.Add(Fact("report", LineageRelation.Writes, null, "arc", "SalesReport"));

        var report = Build(collected);

        Assert.All(report.Objects, o => Assert.Equal("WAREHOUSE", o.Database));
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("", "Given", null, "Given")]
    [InlineData("dwh", "Dwh", null, "Dwh")]
    [InlineData("dwh", "Dwh", "DWH", "DWH")]
    [InlineData("dwh", null, "DWH", "DWH")]
    [InlineData("dwh", "Other", null, "dwh")]
    [InlineData("dwh", null, "Other", "dwh")]
    public void A_part_is_spelled_by_the_connection_then_the_fact_then_the_key(string keyPart, string? given, string? preferred, string? expected)
        => Assert.Equal(expected, LineageGraphBuilder.Spelled(keyPart, given, preferred));

    [Fact]
    public void The_platforms_own_objects_are_not_listed_but_describe_one_a_flow_reads()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("audit"));
        collected.CatalogObjects.Add(new CatalogObject
        {
            ServerRef = "@dwh", Database = "Dwh", Schema = "dbo", Name = "Orders", Kind = LineageNodeKind.Table,
        });
        collected.PlatformObjects.Add(new CatalogObject
        {
            ServerRef = "@dwh", Database = "Dwh", Schema = "catalog", Name = "Run", Kind = LineageNodeKind.Table,
        });
        collected.PlatformObjects.Add(new CatalogObject
        {
            ServerRef = "@dwh", Database = "Dwh", Schema = "catalog", Name = "Pipeline", Kind = LineageNodeKind.Table,
            Columns = [new LineageColumn { Ordinal = 1, Name = "Id", DataType = "uniqueidentifier" }],
        });
        collected.Facts.Add(Fact("audit", LineageRelation.Reads, "Dwh", "catalog", "Pipeline"));

        var report = Build(collected);

        Assert.Equal(["@dwh|dwh|catalog|pipeline", "@dwh|dwh|dbo|orders"], report.Objects.Select(o => o.Key).Order(StringComparer.Ordinal));
        var pipeline = report.Objects.Single(o => o.Name == "Pipeline");
        Assert.Equal(LineageNodeKind.Table, pipeline.Kind);
        Assert.Equal("Id", Assert.Single(pipeline.Columns).Name);
    }

    [Fact]
    public void The_report_names_every_server_read_in_full_with_its_aliases()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(Flow("load"));
        collected.InventoriedServers["@dwh"] = "Dwh";
        collected.ServerAliases["@warehouse"] = "@dwh";
        collected.ServerAliases["@staging"] = "@unreached";

        Assert.Equal(["@dwh", "@warehouse"], Build(collected).InventoriedServers);
    }

    [Fact]
    public void A_database_holding_a_platform_schemas_history_table_hands_over_that_schemas_objects()
    {
        LineagePlatformSchema[] schemas =
        [
            new() { Schema = "catalog", HistoryTable = "__CatalogMigrationsHistory" },
            new() { Schema = "module", HistoryTable = "__EFMigrationsHistory", Objects = ["Ledger"] },
        ];
        var owned = LineagePlatformSchema.OwnedIn(schemas,
        [
            ("Catalog", "__catalogmigrationshistory"), ("catalog", "Run"),
            ("module", "__EFMigrationsHistory"), ("module", "Ledger"), ("module", "Published"), ("dbo", "Orders"),
        ]);

        Assert.True(owned("catalog", "Run"));
        Assert.True(owned("CATALOG", "anything"));
        Assert.True(owned("module", "ledger"));
        Assert.True(owned("module", "__EFMigrationsHistory"));
        Assert.False(owned("module", "Published"));
        Assert.False(owned("dbo", "Orders"));
    }

    [Fact]
    public void A_database_without_a_platform_schemas_history_table_hands_over_nothing()
    {
        var owned = LineagePlatformSchema.OwnedIn(
            [new LineagePlatformSchema { Schema = "catalog", HistoryTable = "__CatalogMigrationsHistory" }],
            [("catalog", "Run"), ("catalog", "Pipeline")]);

        Assert.False(owned("catalog", "Run"));
    }

    [Theory]
    [InlineData("SalesData", "salesdata", false, "SalesData")]
    [InlineData("SalesData", "salesdata", true, "salesdata")]
    [InlineData("salesdata", "SalesData", true, "SalesData")]
    [InlineData("SalesData", "Other", false, "Other")]
    [InlineData(null, "SalesData", false, "SalesData")]
    [InlineData("SalesData", null, false, null)]
    public void A_stored_spelling_changes_case_only_when_the_pass_read_the_database_live(
        string? stored, string? incoming, bool readLive, string? expected)
        => Assert.Equal(expected, SqlFlow.Catalog.CatalogSync.Respelled(stored, incoming, readLive));
}
