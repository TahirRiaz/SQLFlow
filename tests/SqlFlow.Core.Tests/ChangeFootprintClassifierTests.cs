using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ChangeFootprintClassifierTests
{
    private static ChangeFootprint Classify(string from, string to)
        => ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to));

    [Theory]
    [InlineData("int", "bigint")]
    [InlineData("smallint", "int")]
    [InlineData("tinyint", "smallint")]
    public void IntegerRankIncrease_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, Classify(from, to));

    [Theory]
    [InlineData("varchar(50)", "varchar(100)")]
    [InlineData("nvarchar(50)", "nvarchar(100)")]
    public void VariableTextWiden_IsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, Classify(from, to));

    [Theory]
    [InlineData("varchar(50)", "varchar(max)")]
    [InlineData("nvarchar(50)", "nvarchar(max)")]
    [InlineData("varbinary(8)", "varbinary(max)")]
    public void TransitionToMax_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, Classify(from, to));

    [Theory]
    [InlineData("char(10)", "char(20)")]
    [InlineData("nchar(10)", "nchar(20)")]
    [InlineData("binary(8)", "binary(16)")]
    public void FixedWidthWiden_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, Classify(from, to));

    [Fact]
    public void VariableBinaryWiden_IsMetadataOnly()
        => Assert.Equal(ChangeFootprint.MetadataOnly, Classify("varbinary(8)", "varbinary(16)"));

    [Theory]
    [InlineData("decimal(9,2)", "decimal(10,2)")]   // storage class 0 -> 1
    [InlineData("decimal(19,2)", "decimal(20,2)")]  // storage class 1 -> 2
    public void DecimalCrossingStorageClass_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, Classify(from, to));

    [Theory]
    [InlineData("decimal(5,2)", "decimal(9,4)")]    // storage class 0 -> 0
    [InlineData("decimal(10,2)", "decimal(19,4)")]  // storage class 1 -> 1
    public void DecimalWithinStorageClass_IsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, Classify(from, to));

    [Theory]
    [InlineData("datetime2(2)", "datetime2(3)")]  // scale class 0 -> 1
    [InlineData("datetime2(4)", "datetime2(5)")]  // scale class 1 -> 2
    public void DateTimeScaleCrossingClass_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, Classify(from, to));

    [Theory]
    [InlineData("datetime2(0)", "datetime2(2)")]  // scale class 0 -> 0
    [InlineData("datetime2(5)", "datetime2(7)")]  // scale class 2 -> 2
    public void DateTimeScaleWithinClass_IsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, Classify(from, to));
}
