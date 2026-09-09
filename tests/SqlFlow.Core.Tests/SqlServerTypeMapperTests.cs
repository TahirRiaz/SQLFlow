using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SqlServerTypeMapperTests
{
    private static readonly SqlServerTypeMapper Mapper = new();

    [Theory]
    [InlineData(typeof(long), "BIGINT")]
    [InlineData(typeof(int), "INT")]
    [InlineData(typeof(bool), "BIT")]
    [InlineData(typeof(double), "FLOAT")]
    [InlineData(typeof(DateTime), "DATETIME2")]
    [InlineData(typeof(Guid), "UNIQUEIDENTIFIER")]
    public void Map_ClrType_MapsToSqlType(Type clr, string expected)
    {
        var column = Mapper.Map(new SourceColumn { Name = "C", Type = clr }, columnOverride: null, "varchar(255)");

        Assert.Equal(expected, column.SqlType);
    }

    [Fact]
    public void Map_RawString_UsesDefaultColumnType()
    {
        // No inferred length (raw layer) -> falls back to the configured default.
        Assert.Equal("varchar(255)", Mapper.Map(new SourceColumn { Name = "C", Type = typeof(string) }, null, "varchar(255)").SqlType);
        Assert.Equal("varchar(100)", Mapper.Map(new SourceColumn { Name = "C", Type = typeof(string) }, null, "varchar(100)").SqlType);
    }

    [Fact]
    public void Map_String_UsesLengthOrMax()
    {
        Assert.Equal("NVARCHAR(50)", Mapper.Map(new SourceColumn { Name = "C", Type = typeof(string), MaxLength = 50 }, null, "varchar(255)").SqlType);
        Assert.Equal("NVARCHAR(MAX)", Mapper.Map(new SourceColumn { Name = "C", Type = typeof(string), MaxLength = 9000 }, null, "varchar(255)").SqlType);
    }

    [Fact]
    public void Map_Override_WinsOverInference()
    {
        var column = Mapper.Map(
            new SourceColumn { Name = "OrderId", Type = typeof(string) },
            new ColumnOverride { Type = "BIGINT", Nullable = false },
            "varchar(255)");

        Assert.Equal("BIGINT", column.SqlType);
        Assert.False(column.IsNullable);
    }
}
