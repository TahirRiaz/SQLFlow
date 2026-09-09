using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Oracle;

/// <summary>
/// The Oracle type surface, verified against a live database: every row creates a real column of the given
/// Oracle type, introspects it through the production catalog reader, and asserts both the rendered NativeType
/// and the SQL Server type the production mapper derives from it. This catches drift between what Oracle's
/// data dictionary reports and what the mapper expects far more thoroughly than a hand-written NativeType can.
/// Gated on <c>SQLFLOW_TEST_ORACLE</c> only; no sink is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OracleIntrospectionTypeTests
{
    [SkippableTheory]
    // ---- Numbers ----
    [InlineData("NUMBER(10,2)", "decimal(10, 2)")]
    [InlineData("NUMBER(1,0)", "decimal(1, 0)")]
    [InlineData("NUMBER(38,0)", "decimal(38, 0)")]
    [InlineData("NUMBER(38,10)", "decimal(38, 10)")]
    [InlineData("NUMBER(5)", "decimal(5, 0)")]
    [InlineData("NUMBER", "float(53)")]                          // unconstrained NUMBER is a floating numeric
    [InlineData("INTEGER", "decimal(38, 0)")]                     // ANSI INTEGER is NUMBER(*, 0): an integer, not a float
    [InlineData("INT", "decimal(38, 0)")]
    [InlineData("SMALLINT", "decimal(38, 0)")]
    [InlineData("DEC(9,3)", "decimal(9, 3)")]
    [InlineData("NUMERIC(7,2)", "decimal(7, 2)")]
    [InlineData("FLOAT", "float(53)")]
    [InlineData("FLOAT(63)", "float(53)")]
    [InlineData("REAL", "float(53)")]                             // Oracle REAL is FLOAT(63)
    [InlineData("BINARY_FLOAT", "real")]
    [InlineData("BINARY_DOUBLE", "float(53)")]
    // ---- Character ----
    [InlineData("VARCHAR2(50)", "nvarchar(50)")]
    [InlineData("VARCHAR2(1)", "nvarchar(1)")]
    [InlineData("VARCHAR2(4000)", "nvarchar(4000)")]
    [InlineData("VARCHAR2(50 CHAR)", "nvarchar(50)")]
    [InlineData("NVARCHAR2(100)", "nvarchar(100)")]
    [InlineData("CHAR(10)", "nchar(10)")]
    [InlineData("CHAR(1)", "nchar(1)")]
    [InlineData("NCHAR(5)", "nchar(5)")]
    [InlineData("CLOB", "nvarchar(max)")]
    [InlineData("NCLOB", "nvarchar(max)")]
    // ---- Date / time ----
    [InlineData("DATE", "datetime2(0)")]
    [InlineData("TIMESTAMP", "datetime2(6)")]                     // default fractional precision is 6
    [InlineData("TIMESTAMP(0)", "datetime2(0)")]
    [InlineData("TIMESTAMP(3)", "datetime2(3)")]
    [InlineData("TIMESTAMP(6)", "datetime2(6)")]
    [InlineData("TIMESTAMP(9)", "datetime2(7)")]                  // capped at SQL Server's max fractional scale
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "datetimeoffset(6)")]
    [InlineData("TIMESTAMP(9) WITH TIME ZONE", "datetimeoffset(7)")]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", "datetime2(6)")]
    [InlineData("INTERVAL YEAR(3) TO MONTH", "nvarchar(50)")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "nvarchar(50)")]
    // ---- Binary ----
    [InlineData("RAW(16)", "varbinary(16)")]
    [InlineData("RAW(2000)", "varbinary(2000)")]
    [InlineData("BLOB", "varbinary(max)")]
    // ---- Rowid / XML ----
    [InlineData("ROWID", "nvarchar(4000)")]
    [InlineData("XMLTYPE", "xml")]
    public async Task OracleType_IntrospectsAndMaps(string oracleType, string expectedSqlServerType)
    {
        var cs = ForeignDb.Require(DataSourceKind.Oracle);
        var column = await OracleTestSupport.IntrospectColumnAsync(cs, oracleType);

        var mapped = ForeignDb.Mapper(DataSourceKind.Oracle).ToSqlServerType(column);
        Assert.Equal(expectedSqlServerType, mapped);
    }

    [SkippableTheory]
    // The NativeType the reader renders is what the mapper (and every downstream consumer) actually sees.
    [InlineData("NUMBER(10,2)", "NUMBER(10,2)")]
    [InlineData("VARCHAR2(50)", "VARCHAR2(50)")]
    [InlineData("NVARCHAR2(100)", "NVARCHAR2(100)")]
    [InlineData("CHAR(10)", "CHAR(10)")]
    [InlineData("RAW(16)", "RAW(16)")]
    [InlineData("DATE", "DATE")]
    [InlineData("TIMESTAMP(3)", "TIMESTAMP(3)")]
    [InlineData("BINARY_DOUBLE", "BINARY_DOUBLE")]
    public async Task OracleType_RendersExpectedNativeType(string oracleType, string expectedNativeType)
    {
        var cs = ForeignDb.Require(DataSourceKind.Oracle);
        var column = await OracleTestSupport.IntrospectColumnAsync(cs, oracleType);
        Assert.Equal(expectedNativeType, column.NativeType, ignoreCase: true);
    }
}
