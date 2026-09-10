using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

public sealed class RelationalObjectTests
{
    [Fact]
    public void Parse_ThreePart_Bracketed()
    {
        var o = RelationalObject.Parse("[DW].[dbo].[Sales]");
        Assert.Equal("DW", o.Database);
        Assert.Equal("dbo", o.Schema);
        Assert.Equal("Sales", o.Name);
    }

    [Fact]
    public void Parse_ThreePart_Unbracketed_TrimsParts()
    {
        var o = RelationalObject.Parse(" DW . dbo . Sales ");
        Assert.Equal("DW", o.Database);
        Assert.Equal("dbo", o.Schema);
        Assert.Equal("Sales", o.Name);
    }

    [Fact]
    public void Parse_FourPart_DropsLeadingServer()
    {
        var o = RelationalObject.Parse("[srv].[DW].[dbo].[Sales]");
        Assert.Equal("DW", o.Database);
        Assert.Equal("dbo", o.Schema);
        Assert.Equal("Sales", o.Name);
    }

    [Fact]
    public void Parse_BracketedDotsArePreserved()
    {
        var o = RelationalObject.Parse("[my.db].[my.schema].[my.table]");
        Assert.Equal("my.db", o.Database);
        Assert.Equal("my.schema", o.Schema);
        Assert.Equal("my.table", o.Name);
    }

    [Fact]
    public void Parse_EscapedClosingBracket_IsUnescaped()
    {
        var o = RelationalObject.Parse("[DW].[dbo].[Weird]]Name]");
        Assert.Equal("Weird]Name", o.Name);
    }

    [Fact]
    public void QualifiedName_RebracketsAndReescapes()
    {
        var o = RelationalObject.Parse("[DW].[dbo].[Weird]]Name]");
        Assert.Equal("[DW].[dbo].[Weird]]Name]", o.QualifiedName);
    }

    [Theory]
    [InlineData("dbo.Sales")]   // two-part
    [InlineData("Sales")]       // one-part
    [InlineData("DW..Sales")]   // empty schema part
    public void Parse_FewerThanThreePartsOrEmptyPart_Throws(string name)
        => Assert.Throws<SqlFlowException>(() => RelationalObject.Parse(name));

    [Fact]
    public void Parse_Blank_Throws()
        => Assert.Throws<SqlFlowException>(() => RelationalObject.Parse("   "));

    [Fact]
    public void Parse_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => RelationalObject.Parse(null!));
}
