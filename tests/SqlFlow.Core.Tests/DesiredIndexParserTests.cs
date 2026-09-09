using SqlFlow.Core;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

public sealed class DesiredIndexParserTests
{
    [Fact]
    public void Parse_SingleIndex_ExtractsNameSchemaTableAndText()
    {
        var index = Assert.Single(DesiredIndexParser.Parse(
            "CREATE NONCLUSTERED INDEX IX_Orders_Customer ON dbo.Orders (CustomerId);"));

        Assert.Equal("IX_Orders_Customer", index.Name);
        Assert.Equal("dbo", index.Schema);
        Assert.Equal("Orders", index.Table);
        Assert.Contains("CREATE NONCLUSTERED INDEX", index.StatementText, StringComparison.Ordinal);
        Assert.Contains("CustomerId", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MultipleStatements_ReturnsEachIndex()
    {
        var script = """
            CREATE INDEX IX_A ON dbo.Orders (A);
            CREATE UNIQUE INDEX IX_B ON dbo.Orders (B) INCLUDE (C);
            CREATE NONCLUSTERED INDEX IX_C ON sales.Lines (D) WHERE D > 0;
            """;

        var names = DesiredIndexParser.Parse(script).Select(i => i.Name).ToList();

        Assert.Equal(new[] { "IX_A", "IX_B", "IX_C" }, names);
    }

    [Fact]
    public void Parse_PreservesUniqueIncludeAndFilterText()
    {
        var index = Assert.Single(DesiredIndexParser.Parse(
            "CREATE UNIQUE INDEX IX_Filtered ON dbo.Orders (OrderDate) INCLUDE (Amount) WHERE Amount > 0;"));

        Assert.Contains("UNIQUE", index.StatementText, StringComparison.Ordinal);
        Assert.Contains("INCLUDE", index.StatementText, StringComparison.Ordinal);
        Assert.Contains("WHERE", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BracketedSchemaQualifiedName_ResolvesParts()
    {
        var index = Assert.Single(DesiredIndexParser.Parse(
            "CREATE INDEX [IX X] ON [sales].[Order Lines] ([Line Id]);"));

        Assert.Equal("IX X", index.Name);
        Assert.Equal("sales", index.Schema);
        Assert.Equal("Order Lines", index.Table);
    }

    [Fact]
    public void Parse_IgnoresNonIndexStatements()
    {
        var script = """
            -- a comment
            CREATE INDEX IX_A ON dbo.Orders (A);
            """;

        Assert.Single(DesiredIndexParser.Parse(script));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyScript_ReturnsNothing(string script)
        => Assert.Empty(DesiredIndexParser.Parse(script));

    [Fact]
    public void Parse_InvalidSql_ThrowsWithDetail()
    {
        var error = Assert.Throws<SqlFlowException>(() => DesiredIndexParser.Parse("CREATE INDEX ON ;;("));

        Assert.Contains("desired-index", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
