using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.MySql;

/// <summary>
/// The MySQL type surface, verified against a live database: every row creates a real column of the given
/// MySQL type, introspects it through the production catalog reader, and asserts the SQL Server type the
/// production mapper derives from the rendered <c>COLUMN_TYPE</c>. This catches drift between what MySQL's
/// information_schema reports (for example <c>int unsigned</c>, <c>tinyint(1)</c>, <c>varchar(50)</c>) and
/// what the mapper expects far more thoroughly than a hand-written NativeType can. Gated on
/// <c>SQLFLOW_TEST_MYSQL</c> only; no sink is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlIntrospectionTypeTests
{
    [SkippableTheory]
    // ---- Integers (signed vs unsigned; tinyint(1) is MySQL's boolean idiom) ----
    [InlineData("tinyint", "smallint")]                       // MySQL tinyint is signed (-128..127)
    [InlineData("tinyint(1)", "bit")]                         // the boolean idiom
    [InlineData("tinyint unsigned", "tinyint")]
    [InlineData("smallint", "smallint")]
    [InlineData("smallint unsigned", "int")]
    [InlineData("mediumint", "int")]
    [InlineData("mediumint unsigned", "int")]
    [InlineData("int", "int")]
    [InlineData("int unsigned", "bigint")]
    [InlineData("bigint", "bigint")]
    [InlineData("bigint unsigned", "decimal(20, 0)")]         // deviation: SSMA's bigint overflows above 2^63-1
    // ---- Fixed and floating point ----
    [InlineData("decimal(10,2)", "decimal(10, 2)")]
    [InlineData("numeric(18,4)", "decimal(18, 4)")]
    [InlineData("float", "real")]
    [InlineData("double", "float(53)")]
    [InlineData("bit(1)", "bit")]
    [InlineData("bit(12)", "binary(2)")]                      // 12 bits round up to 2 bytes
    // ---- Date / time ----
    [InlineData("date", "date")]
    [InlineData("datetime(3)", "datetime2(3)")]
    [InlineData("timestamp(6)", "datetime2(6)")]              // deviation: not legacy datetime
    [InlineData("time(3)", "time(3)")]
    [InlineData("year", "smallint")]
    // ---- Character ----
    [InlineData("char(10)", "nchar(10)")]
    [InlineData("varchar(255)", "nvarchar(255)")]
    [InlineData("varchar(5000)", "nvarchar(max)")]           // beyond nvarchar(4000) overflows to max
    [InlineData("tinytext", "nvarchar(255)")]
    [InlineData("text", "nvarchar(max)")]
    [InlineData("mediumtext", "nvarchar(max)")]
    [InlineData("longtext", "nvarchar(max)")]
    // ---- Binary ----
    [InlineData("binary(16)", "binary(16)")]
    [InlineData("varbinary(9000)", "varbinary(max)")]        // beyond varbinary(8000) overflows to max
    [InlineData("tinyblob", "varbinary(255)")]
    [InlineData("blob", "varbinary(max)")]
    // ---- Enum / set / json / spatial ----
    [InlineData("enum('a','b')", "nvarchar(255)")]
    [InlineData("set('a','b')", "nvarchar(max)")]
    [InlineData("json", "nvarchar(max)")]
    [InlineData("geometry", "varbinary(max)")]
    public async Task MySqlType_IntrospectsAndMaps(string mysqlType, string expectedSqlServerType)
    {
        var cs = ForeignDb.Require(DataSourceKind.MySQL);
        var column = await MySqlTestSupport.IntrospectColumnAsync(cs, mysqlType);

        var mapped = ForeignDb.Mapper(DataSourceKind.MySQL).ToSqlServerType(column);
        Assert.Equal(expectedSqlServerType, mapped);
    }

    [SkippableTheory]
    // The NativeType the reader renders is the full COLUMN_TYPE string, which is what the mapper (and every
    // downstream consumer) actually sees. MySQL lower-cases these renderings.
    [InlineData("INT UNSIGNED", "int unsigned")]
    [InlineData("VARCHAR(50)", "varchar(50)")]
    [InlineData("DECIMAL(10,2)", "decimal(10,2)")]
    [InlineData("TINYINT(1)", "tinyint(1)")]
    [InlineData("BIGINT UNSIGNED", "bigint unsigned")]
    [InlineData("CHAR(10)", "char(10)")]
    [InlineData("DATETIME(3)", "datetime(3)")]
    [InlineData("VARBINARY(16)", "varbinary(16)")]
    public async Task MySqlType_RendersExpectedNativeType(string mysqlType, string expectedNativeType)
    {
        var cs = ForeignDb.Require(DataSourceKind.MySQL);
        var column = await MySqlTestSupport.IntrospectColumnAsync(cs, mysqlType);
        Assert.Equal(expectedNativeType, column.NativeType, ignoreCase: true);
    }
}
