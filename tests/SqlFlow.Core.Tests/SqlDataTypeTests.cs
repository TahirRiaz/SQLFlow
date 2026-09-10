using SqlFlow.Core;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SqlDataTypeTests
{
    [Theory]
    [InlineData("int", "int")]
    [InlineData("INT", "int")]
    [InlineData("[bigint]", "bigint")]
    [InlineData("nvarchar(50)", "nvarchar(50)")]
    [InlineData("NVARCHAR (50)", "nvarchar(50)")]
    [InlineData("varchar(max)", "varchar(max)")]
    [InlineData("VARBINARY(MAX)", "varbinary(max)")]
    [InlineData("decimal(18,2)", "decimal(18, 2)")]
    [InlineData("decimal(18, 2)", "decimal(18, 2)")]
    [InlineData("datetime2(3)", "datetime2(3)")]
    [InlineData("uniqueidentifier", "uniqueidentifier")]
    public void Parse_Then_Render_RoundTrips(string input, string expected)
        => Assert.Equal(expected, SqlDataType.Parse(input).Render());

    [Theory]
    [InlineData("numeric(10,2)", "decimal")]
    [InlineData("integer", "int")]
    [InlineData("rowversion", "timestamp")]
    public void Parse_NormalizesSynonyms(string input, string expectedBase)
        => Assert.Equal(expectedBase, SqlDataType.Parse(input).BaseType);

    [Fact]
    public void Parse_Decimal_PrecisionThenScale_NotSwapped()
    {
        var t = SqlDataType.Parse("decimal(18, 4)");
        Assert.Equal(18, t.Precision);
        Assert.Equal(4, t.Scale);
    }

    [Fact]
    public void Parse_Max_IsNegativeOne()
    {
        var t = SqlDataType.Parse("nvarchar(max)");
        Assert.True(t.IsMax);
        Assert.Equal(-1, t.Length);
    }

    [Theory]
    [InlineData("nvarchar(50)", SqlTypeFamily.Text)]
    [InlineData("bigint", SqlTypeFamily.Integer)]
    [InlineData("decimal(18,2)", SqlTypeFamily.Decimal)]
    [InlineData("float", SqlTypeFamily.Approximate)]
    [InlineData("money", SqlTypeFamily.Money)]
    [InlineData("datetime2(7)", SqlTypeFamily.DateTime)]
    [InlineData("bit", SqlTypeFamily.Bit)]
    [InlineData("varbinary(64)", SqlTypeFamily.Binary)]
    [InlineData("uniqueidentifier", SqlTypeFamily.Guid)]
    [InlineData("xml", SqlTypeFamily.Other)]
    public void Family_Classifies(string input, SqlTypeFamily expected)
        => Assert.Equal(expected, SqlDataType.Parse(input).Family);

    [Theory]
    [InlineData("")]
    [InlineData("nvarchar(abc)")]
    public void Parse_Malformed_Throws(string input)
        => Assert.Throws<SqlFlowException>(() => SqlDataType.Parse(input));

    [Fact]
    public void Parse_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => SqlDataType.Parse(null!));
}
