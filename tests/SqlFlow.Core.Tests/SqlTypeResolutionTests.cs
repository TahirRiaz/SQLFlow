using SqlFlow.Core;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SqlTypeResolutionTests
{
    private static SqlTypeResolutionResult Resolve(string existing, string desired)
        => SqlTypeResolution.Resolve(SqlDataType.Parse(existing), SqlDataType.Parse(desired));

    [Fact]
    public void SameType_IsKept()
        => Assert.Equal(SchemaChangeAction.Keep, Resolve("nvarchar(50)", "nvarchar(50)").Action);

    [Theory]
    [InlineData("varchar(100)", "varchar(50)")]   // target already longer
    [InlineData("bigint", "int")]                 // target already wider integer
    [InlineData("nvarchar(50)", "varchar(50)")]   // target already unicode (superset)
    [InlineData("decimal(18,4)", "decimal(10,2)")]// target already covers
    [InlineData("datetime2(7)", "datetime2(3)")]  // target already finer
    public void TargetAlreadyAccommodates_IsKept(string existing, string desired)
        => Assert.Equal(SchemaChangeAction.Keep, Resolve(existing, desired).Action);

    [Theory]
    [InlineData("varchar(50)", "varchar(100)", "varchar(100)")]    // length grows
    [InlineData("varchar(50)", "varchar(max)", "varchar(max)")]    // grows to max
    [InlineData("varchar(50)", "nvarchar(50)", "nvarchar(50)")]    // widen to unicode
    [InlineData("int", "bigint", "bigint")]                        // integer promotion
    [InlineData("smallint", "int", "int")]
    [InlineData("datetime2(3)", "datetime2(7)", "datetime2(7)")]   // datetime scale grows
    [InlineData("binary(8)", "varbinary(16)", "varbinary(16)")]    // binary widen
    public void IncomingIsWider_AltersToMerged(string existing, string desired, string expected)
    {
        var result = Resolve(existing, desired);
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal(expected, result.Merged!.Render());
    }

    [Fact]
    public void Decimal_MergesPrecisionAndScale_Independently()
    {
        // existing keeps 14 integer digits (18-4); desired needs 2 scale -> merged keeps both ranges.
        var result = Resolve("decimal(18,4)", "decimal(10,6)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("decimal(20, 6)", result.Merged!.Render()); // 14 integer digits + 6 scale
    }

    [Theory]
    [InlineData("int", "nvarchar(50)")]    // integer vs text
    [InlineData("datetime", "int")]        // datetime vs integer
    [InlineData("date", "datetime2(7)")]   // differing date/time bases (conservative)
    [InlineData("timestamp", "varbinary(8)")] // rowversion cannot be altered
    public void CrossFamilyOrUnsafe_IsIncompatible(string existing, string desired)
    {
        var result = Resolve(existing, desired);
        Assert.Equal(SchemaChangeAction.Incompatible, result.Action);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }
}

public sealed class HashKeyTests
{
    [Theory]
    [InlineData("SHA2_512", "binary(64)")]
    [InlineData("SHA2_256", "binary(32)")]
    [InlineData("sha1", "binary(20)")]
    [InlineData("MD5", "binary(16)")]
    public void BinaryTypeFor_KnownAlgorithms(string algorithm, string expected)
        => Assert.Equal(expected, HashKey.BinaryTypeFor(algorithm).Render());

    [Fact]
    public void BinaryTypeFor_NullOrBlank_DefaultsToSha2_256()
    {
        Assert.Equal("binary(32)", HashKey.BinaryTypeFor(null).Render());
        Assert.Equal("binary(32)", HashKey.BinaryTypeFor("  ").Render());
    }

    [Fact]
    public void BinaryTypeFor_Unknown_Throws()
        => Assert.Throws<SqlFlowException>(() => HashKey.BinaryTypeFor("CRC32"));
}
