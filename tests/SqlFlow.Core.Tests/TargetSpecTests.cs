using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

public sealed class TargetSpecTests
{
    [Fact]
    public void QualifiedName_PlainName()
    {
        var target = new TargetSpec { Connection = "@x", Schema = "dbo", Table = "Sales" };
        Assert.Equal("[dbo].[Sales]", target.QualifiedName);
    }

    [Fact]
    public void QualifiedName_EscapesClosingBracket()
    {
        var target = new TargetSpec { Connection = "@x", Schema = "dbo", Table = "My]Tbl" };
        Assert.Equal("[dbo].[My]]Tbl]", target.QualifiedName);
    }
}
