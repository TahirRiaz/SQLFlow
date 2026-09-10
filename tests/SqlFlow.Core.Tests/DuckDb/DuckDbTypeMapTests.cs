using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>The DuckDB-to-SQL-Server type mapping: faithful scalar mappings, decimal precision/scale preserved
/// and clamped to SQL Server's ceiling, unsigned widths widened to a signed type that holds them, and nested
/// types flagged for JSON projection.</summary>
public sealed class DuckDbTypeMapTests
{
    [Theory]
    [InlineData("BOOLEAN", "bit")]
    [InlineData("TINYINT", "smallint")]      // DuckDB TINYINT is signed; SQL tinyint is unsigned
    [InlineData("UTINYINT", "tinyint")]
    [InlineData("SMALLINT", "smallint")]
    [InlineData("USMALLINT", "int")]
    [InlineData("INTEGER", "int")]
    [InlineData("UINTEGER", "bigint")]
    [InlineData("BIGINT", "bigint")]
    [InlineData("UBIGINT", "decimal(20,0)")]
    [InlineData("HUGEINT", "decimal(38,0)")]
    [InlineData("FLOAT", "real")]
    [InlineData("DOUBLE", "float")]
    [InlineData("VARCHAR", "nvarchar(max)")]
    [InlineData("BLOB", "varbinary(max)")]
    [InlineData("DATE", "date")]
    [InlineData("TIMESTAMP", "datetime2")]
    [InlineData("TIMESTAMP WITH TIME ZONE", "datetimeoffset")]
    [InlineData("UUID", "uniqueidentifier")]
    [InlineData("JSON", "nvarchar(max)")]
    public void MapsScalarTypes(string duckType, string expectedSql)
        => Assert.Equal(expectedSql, DuckDbTypeMap.Map(duckType).SqlType);

    [Fact]
    public void Decimal_PreservesPrecisionAndScale()
    {
        var mapped = DuckDbTypeMap.Map("DECIMAL(18,4)");
        Assert.Equal("decimal(18,4)", mapped.SqlType);
        Assert.Equal(18, mapped.Precision);
        Assert.Equal(4, mapped.Scale);
        Assert.Equal(typeof(decimal), mapped.ClrType);
    }

    [Fact]
    public void Decimal_ClampsBeyondSqlServerCeiling()
    {
        // SQL Server's decimal precision ceiling is 38.
        Assert.Equal("decimal(38,10)", DuckDbTypeMap.Map("DECIMAL(50,10)").SqlType);
    }

    [Theory]
    [InlineData("INTEGER[]")]
    [InlineData("VARCHAR[]")]
    [InlineData("STRUCT(a INTEGER, b VARCHAR)")]
    [InlineData("MAP(VARCHAR, INTEGER)")]
    public void NestedTypes_AreFlaggedForJsonProjection(string duckType)
    {
        var mapped = DuckDbTypeMap.Map(duckType);
        Assert.True(mapped.IsNested);
        Assert.Equal("nvarchar(max)", mapped.SqlType);
    }

    [Fact]
    public void SizedVarchar_KeepsLength()
        => Assert.Equal("nvarchar(100)", DuckDbTypeMap.Map("VARCHAR(100)").SqlType);

    [Fact]
    public void UnknownType_FallsBackToText()
        => Assert.Equal("nvarchar(max)", DuckDbTypeMap.Map("SOME_FUTURE_TYPE").SqlType);
}
