using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// A resolved AI context version: stored as JSON that always reads the same for the same version (its hash is what an
/// admin approves), and narrowed by what the file proposes now, so a removal is served at once while an addition or a
/// change waits for approval.
/// </summary>
public sealed class AiContextVersionTests
{
    private static AiContextVersion Approved() => new(
        "sales", "Sales mart.", "Amounts exclude tax.",
        [
            new AiContextVersionTable(
                "k|dw|sales|orders", "dw.sales.Orders", "Order lines", "One row per line.", ["sales"], ["OrderId"],
                [
                    new AiContextVersionColumn("OrderId", null, []),
                    new AiContextVersionColumn("Amount", "Line amount.", ["revenue"]),
                    new AiContextVersionColumn("CustomerId", null, []),
                ],
                ["CostPrice"],
                [new AiContextVersionMeasure("net_revenue", "SUM(Amount)", null), new AiContextVersionMeasure("lines", "COUNT(OrderId)", null)],
                [new AiContextVersionJoin("k|dw|sales|customers", "dw.sales.Customers", ["CustomerId"], ["Id"], SemanticJoinType.Left, null)]),
            new AiContextVersionTable(
                "k|dw|sales|customers", "dw.sales.Customers", null, null, [], [],
                [new AiContextVersionColumn("Id", null, []), new AiContextVersionColumn("Name", null, [])], ["Email"], [], []),
        ],
        [
            new AiContextVersionExample("Revenue last month?", "SELECT SUM(Amount) FROM sales.Orders", "@dw", ["k|dw|sales|orders"]),
            new AiContextVersionExample("How many customers?", "SELECT COUNT(*) FROM sales.Customers", null, ["k|dw|sales|customers"]),
        ]);

    [Fact]
    public void TheJson_ReadsBackAsTheSameVersion_AndTheSameVersionHashesAlike()
    {
        var version = Approved();
        var json = version.ToJson();
        var reread = AiContextVersion.FromJson(json);

        Assert.Equal(json, reread.ToJson());
        Assert.Equal(AiContextVersion.HashOf(json), AiContextVersion.HashOf(Approved().ToJson()));
        Assert.Equal(64, AiContextVersion.HashOf(json).Length);
        Assert.Matches("^[0-9a-f]{64}$", AiContextVersion.HashOf(json));
        Assert.DoesNotContain("identity", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnyChange_IsAnotherVersion()
    {
        var hash = AiContextVersion.HashOf(Approved().ToJson());
        var described = Approved() with { Description = "Sales mart, renamed." };
        var moreColumns = Approved() with
        {
            Tables = [Approved().Tables[0] with { Columns = [.. Approved().Tables[0].Columns, new AiContextVersionColumn("Tax", null, [])] }, Approved().Tables[1]],
        };

        Assert.NotEqual(hash, AiContextVersion.HashOf(described.ToJson()));
        Assert.NotEqual(hash, AiContextVersion.HashOf(moreColumns.ToJson()));
    }

    [Fact]
    public void FromJson_RefusesNull()
        => Assert.Throws<System.Text.Json.JsonException>(() => AiContextVersion.FromJson("null"));

    [Fact]
    public void Narrowing_ByTheSameVersion_ChangesNothing()
        => Assert.Equal(Approved().ToJson(), Approved().NarrowedTo(Approved()).ToJson());

    [Fact]
    public void Narrowing_ServesOnlyWhatTheFileStillGives_AsTheApprovedVersionGivesIt()
    {
        var approved = Approved();
        var orders = approved.Tables[0];
        // The file now drops the Amount column, the lines measure, the Customers table and one example, changes the
        // description of a column it keeps, and adds a column, a measure and an example.
        var current = approved with
        {
            Instructions = "Changed instructions.",
            Tables =
            [
                orders with
                {
                    Columns =
                    [
                        new AiContextVersionColumn("orderid", "Now described.", []),
                        new AiContextVersionColumn("CustomerId", null, []),
                        new AiContextVersionColumn("Added", null, []),
                    ],
                    Measures = [new AiContextVersionMeasure("NET_REVENUE", "SUM(Changed)", null), new AiContextVersionMeasure("new_one", "1", null)],
                },
            ],
            Examples = [approved.Examples[0] with { Sql = "SELECT 2" }, new AiContextVersionExample("New?", "SELECT 3", null, [])],
        };

        var effective = approved.NarrowedTo(current);

        Assert.Equal("Amounts exclude tax.", effective.Instructions);
        var table = Assert.Single(effective.Tables);
        Assert.Equal(["OrderId", "CustomerId"], table.Columns.Select(c => c.Name));
        Assert.Null(table.Columns[0].Description);
        // A column the file now denies or no longer has is reported as denied by what is served.
        Assert.Equal(["CostPrice", "Amount"], table.Denied);
        var measure = Assert.Single(table.Measures);
        Assert.Equal("SUM(Amount)", measure.Expression);
        Assert.Single(table.Joins);
        var example = Assert.Single(effective.Examples);
        Assert.Equal("SELECT SUM(Amount) FROM sales.Orders", example.Sql);
    }

    [Fact]
    public void Narrowing_DropsATableLeftWithNoColumn_AndInstructionsTheFileNoLongerGives()
    {
        var approved = Approved();
        var current = approved with
        {
            Instructions = null,
            Tables = [approved.Tables[0] with { Columns = [new AiContextVersionColumn("Unrelated", null, [])] }, approved.Tables[1]],
        };

        var effective = approved.NarrowedTo(current);

        Assert.Null(effective.Instructions);
        Assert.Equal(["k|dw|sales|customers"], effective.Tables.Select(t => t.ObjectKey));
    }

    [Fact]
    public void Narrowing_KeepsAJoinOnlyWhileTheFileDeclaresTheSameOne()
    {
        var approved = Approved();
        var orders = approved.Tables[0];
        var otherColumns = approved with
        {
            Tables = [orders with { Joins = [orders.Joins[0] with { ToColumns = ["OtherId"] }] }, approved.Tables[1]],
        };
        var otherType = approved with
        {
            Tables = [orders with { Joins = [orders.Joins[0] with { JoinType = SemanticJoinType.Inner }] }, approved.Tables[1]],
        };

        Assert.Empty(approved.NarrowedTo(otherColumns).Tables[0].Joins);
        Assert.Equal(SemanticJoinType.Left, Assert.Single(approved.NarrowedTo(otherType).Tables[0].Joins).JoinType);
    }

    [Fact]
    public void TheJoinIdentity_IsTheSameWhicheverEndIsAskedAbout()
    {
        var forward = SemanticRelationshipIdentity.Compute("a", ["x"], "b", ["y"]);
        Assert.Equal(forward, SemanticRelationshipIdentity.Compute("a", ["X"], "b", ["Y"]));
        Assert.NotEqual(forward, SemanticRelationshipIdentity.Compute("b", ["y"], "a", ["x"]));
    }
}
