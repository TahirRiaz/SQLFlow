using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Postgres;

/// <summary>
/// The PostgreSQL type surface, verified against a live database: every row creates a real column of the given
/// PostgreSQL type, introspects it through the production catalog reader, and asserts both the rendered
/// NativeType and the SQL Server type the production mapper derives from it. PostgreSQL stores most types under
/// internal <c>udt_name</c> values (integer becomes <c>int4</c>, boolean becomes <c>bool</c>, character varying
/// becomes <c>varchar</c>, and so on), so this catches drift between what <c>information_schema</c> reports and
/// what the mapper expects far more thoroughly than a hand-written NativeType can. Gated on <c>SQLFLOW_TEST_PG</c>
/// only; no sink is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresIntrospectionTypeTests
{
    [SkippableTheory]
    // ---- Booleans / integers ----
    [InlineData("boolean", "bit")]                               // udt bool
    [InlineData("smallint", "smallint")]                         // udt int2
    [InlineData("int2", "smallint")]
    [InlineData("integer", "int")]                               // udt int4
    [InlineData("int4", "int")]
    [InlineData("bigint", "bigint")]                             // udt int8
    [InlineData("int8", "bigint")]
    [InlineData("oid", "bigint")]
    [InlineData("serial", "int")]                                // int4 with a nextval default
    [InlineData("bigserial", "bigint")]                          // int8 with a nextval default
    // ---- Exact / approximate numerics ----
    [InlineData("numeric(10,2)", "decimal(10, 2)")]
    [InlineData("numeric(38,10)", "decimal(38, 10)")]
    [InlineData("decimal(9,3)", "decimal(9, 3)")]
    [InlineData("real", "real")]                                 // udt float4
    [InlineData("float4", "real")]
    [InlineData("double precision", "float(53)")]                // udt float8
    [InlineData("float8", "float(53)")]
    [InlineData("money", "decimal(19, 2)")]
    // ---- Date / time ----
    [InlineData("date", "date")]
    [InlineData("timestamp(6)", "datetime2(6)")]
    [InlineData("timestamp(3)", "datetime2(3)")]
    [InlineData("timestamp with time zone", "datetimeoffset(6)")]  // udt timestamptz(6): a UTC instant keeps its offset type
    [InlineData("timestamptz(3)", "datetimeoffset(3)")]
    [InlineData("time(3)", "time(3)")]
    [InlineData("timetz", "nvarchar(50)")]                       // time with time zone rides as text
    [InlineData("interval", "nvarchar(50)")]
    // ---- Character ----
    [InlineData("char(10)", "nchar(10)")]                        // udt bpchar(10)
    [InlineData("character(5)", "nchar(5)")]
    [InlineData("varchar(50)", "nvarchar(50)")]
    [InlineData("character varying(100)", "nvarchar(100)")]
    [InlineData("varchar", "nvarchar(max)")]                     // unbounded character varying
    [InlineData("text", "nvarchar(max)")]
    // ---- Binary / identity / structured ----
    [InlineData("bytea", "varbinary(max)")]
    [InlineData("uuid", "uniqueidentifier")]
    [InlineData("json", "nvarchar(max)")]
    [InlineData("jsonb", "nvarchar(max)")]
    [InlineData("xml", "xml")]
    [InlineData("inet", "nvarchar(max)")]
    [InlineData("integer[]", "nvarchar(max)")]                   // udt _int4: arrays ride as text
    public async Task PostgresType_IntrospectsAndMaps(string pgType, string expectedSqlServerType)
    {
        var cs = ForeignDb.Require(DataSourceKind.PostgreSQL);
        var column = await PostgresTestSupport.IntrospectColumnAsync(cs, pgType);

        var mapped = ForeignDb.Mapper(DataSourceKind.PostgreSQL).ToSqlServerType(column);
        Assert.Equal(expectedSqlServerType, mapped);
    }

    [SkippableTheory]
    // The NativeType the reader renders from udt_name plus modifiers is what the mapper (and every downstream
    // consumer) actually sees. PostgreSQL reports the internal udt name, not the SQL spelling of the DDL type.
    [InlineData("integer", "int4")]
    [InlineData("bigint", "int8")]
    [InlineData("smallint", "int2")]
    [InlineData("boolean", "bool")]
    [InlineData("real", "float4")]
    [InlineData("double precision", "float8")]
    [InlineData("character varying(50)", "varchar(50)")]
    [InlineData("char(10)", "bpchar(10)")]
    [InlineData("numeric(10,2)", "numeric(10,2)")]
    [InlineData("timestamp with time zone", "timestamptz(6)")]
    [InlineData("bytea", "bytea")]
    [InlineData("integer[]", "_int4")]
    public async Task PostgresType_RendersExpectedNativeType(string pgType, string expectedNativeType)
    {
        var cs = ForeignDb.Require(DataSourceKind.PostgreSQL);
        var column = await PostgresTestSupport.IntrospectColumnAsync(cs, pgType);
        Assert.Equal(expectedNativeType, column.NativeType, ignoreCase: true);
    }
}
