using MySqlConnector;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers.MySql;
using SqlFlow.Providers.Oracle;
using SqlFlow.Providers.Postgres;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The provider seams that need no live database: type mappers (every mapping row), SQL dialects,
/// canonicalizer gates, and the composite factories' closed-registry errors.</summary>
public sealed class SourceProviderTests
{
    private static CatalogColumn Col(string nativeType) => new() { Name = "C", Ordinal = 1, NativeType = nativeType };

    // ---- MySQL type mapping (SSMA-grounded, with the documented deviations) ----

    [Theory]
    [InlineData("tinyint", "smallint")]                       // MySQL tinyint is signed
    [InlineData("tinyint(1)", "bit")]                         // boolean idiom
    [InlineData("tinyint unsigned", "tinyint")]
    [InlineData("smallint", "smallint")]
    [InlineData("smallint unsigned", "int")]
    [InlineData("mediumint", "int")]
    [InlineData("mediumint unsigned", "int")]
    [InlineData("int", "int")]
    [InlineData("int unsigned", "bigint")]
    [InlineData("bigint", "bigint")]
    [InlineData("bigint unsigned", "decimal(20, 0)")]         // deviation: SSMA's bigint overflows
    [InlineData("decimal(10,2)", "decimal(10, 2)")]
    [InlineData("numeric(18,4)", "decimal(18, 4)")]
    [InlineData("float", "real")]
    [InlineData("double", "float(53)")]
    [InlineData("bit(1)", "bit")]
    [InlineData("bit(12)", "binary(2)")]
    [InlineData("date", "date")]
    [InlineData("datetime(3)", "datetime2(3)")]
    [InlineData("timestamp(6)", "datetime2(6)")]              // deviation: not legacy datetime
    [InlineData("time(3)", "time(3)")]
    [InlineData("year", "smallint")]
    [InlineData("char(10)", "nchar(10)")]
    [InlineData("char(36)", "nchar(36)")]                     // MySQL has no UUID type: char(36) stays text
    [InlineData("varchar(255)", "nvarchar(255)")]
    [InlineData("varchar(5000)", "nvarchar(max)")]
    [InlineData("tinytext", "nvarchar(255)")]
    [InlineData("longtext", "nvarchar(max)")]
    [InlineData("binary(16)", "binary(16)")]
    [InlineData("varbinary(9000)", "varbinary(max)")]
    [InlineData("blob", "varbinary(max)")]
    [InlineData("enum('a','b')", "nvarchar(255)")]
    [InlineData("set('a','b')", "nvarchar(max)")]
    [InlineData("json", "nvarchar(max)")]
    [InlineData("geometry", "varbinary(max)")]
    public void MySqlTypes_MapToSqlServer(string mysql, string expected)
        => Assert.Equal(expected, new MySqlSourceTypeMapper().ToSqlServerType(Col(mysql)));

    [Theory]
    [InlineData("decimal(40,2)")]   // precision beyond SQL Server's 38: silent capping would corrupt data
    [InlineData("frobnicate")]      // unknown type: must throw, never degrade
    public void MySqlTypes_Unmappable_Throw(string mysql)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new MySqlSourceTypeMapper().ToSqlServerType(Col(mysql)));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- PostgreSQL type mapping ----

    [Theory]
    [InlineData("bool", "bit")]
    [InlineData("int2", "smallint")]
    [InlineData("int4", "int")]
    [InlineData("int8", "bigint")]
    [InlineData("oid", "bigint")]
    [InlineData("numeric(10,2)", "decimal(10, 2)")]
    [InlineData("float4", "real")]
    [InlineData("float8", "float(53)")]
    [InlineData("money", "decimal(19, 2)")]
    [InlineData("date", "date")]
    [InlineData("timestamp(6)", "datetime2(6)")]
    [InlineData("timestamptz(6)", "datetimeoffset(6)")]       // a UTC instant keeps its offset type
    [InlineData("time(3)", "time(3)")]
    [InlineData("timetz", "nvarchar(50)")]
    [InlineData("interval", "nvarchar(50)")]
    [InlineData("bpchar(10)", "nchar(10)")]
    [InlineData("varchar(50)", "nvarchar(50)")]
    [InlineData("varchar", "nvarchar(max)")]                  // unmodified varchar is unlimited
    [InlineData("text", "nvarchar(max)")]
    [InlineData("bytea", "varbinary(max)")]
    [InlineData("uuid", "uniqueidentifier")]
    [InlineData("json", "nvarchar(max)")]
    [InlineData("jsonb", "nvarchar(max)")]
    [InlineData("xml", "xml")]
    [InlineData("_int4", "nvarchar(max)")]                    // arrays ride as text
    [InlineData("inet", "nvarchar(max)")]
    public void PostgresTypes_MapToSqlServer(string postgres, string expected)
        => Assert.Equal(expected, new PostgresSourceTypeMapper().ToSqlServerType(Col(postgres)));

    [Theory]
    [InlineData("numeric")]         // unbounded numeric: needs explicit precision
    [InlineData("numeric(50,2)")]   // precision beyond 38
    [InlineData("frobnicate")]
    public void PostgresTypes_Unmappable_Throw(string postgres)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new PostgresSourceTypeMapper().ToSqlServerType(Col(postgres)));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- Oracle type mapping (SSMA-grounded, with the documented decisions) ----

    [Theory]
    [InlineData("NUMBER(10,2)", "decimal(10, 2)")]
    [InlineData("NUMBER(38,0)", "decimal(38, 0)")]
    [InlineData("NUMBER", "float(53)")]                        // unconstrained NUMBER is a floating numeric
    [InlineData("FLOAT", "float(53)")]
    [InlineData("FLOAT(126)", "float(53)")]
    [InlineData("BINARY_FLOAT", "real")]
    [InlineData("BINARY_DOUBLE", "float(53)")]
    [InlineData("VARCHAR2(50)", "nvarchar(50)")]
    [InlineData("VARCHAR2(5000)", "nvarchar(max)")]
    [InlineData("NVARCHAR2(100)", "nvarchar(100)")]
    [InlineData("CHAR(10)", "nchar(10)")]
    [InlineData("NCHAR(5)", "nchar(5)")]
    [InlineData("CLOB", "nvarchar(max)")]
    [InlineData("NCLOB", "nvarchar(max)")]
    [InlineData("LONG", "nvarchar(max)")]
    [InlineData("DATE", "datetime2(0)")]                       // Oracle DATE carries time to whole seconds
    [InlineData("TIMESTAMP(6)", "datetime2(6)")]
    [InlineData("TIMESTAMP(9)", "datetime2(7)")]               // capped at SQL Server's max fractional scale
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "datetimeoffset(6)")]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", "datetime2(6)")]  // normalized instant, no stored offset
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "nvarchar(50)")]
    [InlineData("INTERVAL YEAR(2) TO MONTH", "nvarchar(50)")]
    [InlineData("RAW(16)", "varbinary(16)")]
    [InlineData("RAW(9000)", "varbinary(max)")]
    [InlineData("BLOB", "varbinary(max)")]
    [InlineData("LONG RAW", "varbinary(max)")]
    [InlineData("ROWID", "nvarchar(4000)")]
    [InlineData("XMLTYPE", "xml")]
    public void OracleTypes_MapToSqlServer(string oracle, string expected)
        => Assert.Equal(expected, new OracleSourceTypeMapper().ToSqlServerType(Col(oracle)));

    [Theory]
    [InlineData("BFILE")]           // an external file locator, not row data
    [InlineData("SDO_GEOMETRY")]    // a spatial object type with no scalar mapping
    [InlineData("FROBNICATE")]      // unknown type: must throw, never degrade
    public void OracleTypes_Unmappable_Throw(string oracle)
    {
        var ex = Assert.Throws<SqlFlowException>(() => new OracleSourceTypeMapper().ToSqlServerType(Col(oracle)));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- Dialects ----

    private static readonly RelationalObject Table = new() { Database = "erp", Schema = "erp", Name = "orders" };

    [Fact]
    public void MySqlDialect_Backticks_DateSub_DatabaseQualification()
    {
        var dialect = new MySqlSourceDialect();
        Assert.Equal("`o``dd`", dialect.QuoteIdentifier("o`dd"));
        Assert.Equal("`erp`.`orders`", dialect.QualifyObject(Table));    // schema part IS the database
        Assert.Equal("DATE_SUB(MIN(`d`), INTERVAL 7 DAY)", dialect.DateSubtractDays("MIN(`d`)", 7));
        Assert.Equal("0xDEAD", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    [Fact]
    public void PostgresDialect_DoubleQuotes_Interval_ByteaLiteral()
    {
        var dialect = new PostgresSourceDialect();
        Assert.Equal("\"o\"\"dd\"", dialect.QuoteIdentifier("o\"dd"));
        Assert.Equal("\"erp\".\"orders\"", dialect.QualifyObject(Table));
        Assert.Equal("(MIN(\"d\") - INTERVAL '7 days')", dialect.DateSubtractDays("MIN(\"d\")", 7));
        Assert.Equal("'\\xDEAD'::bytea", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    [Fact]
    public void OracleDialect_DoubleQuotes_NumToDsInterval_HexToRaw()
    {
        var dialect = new OracleSourceDialect();
        Assert.Equal("\"o\"\"dd\"", dialect.QuoteIdentifier("o\"dd"));
        Assert.Equal("\"erp\".\"orders\"", dialect.QualifyObject(Table));
        Assert.Equal("(MIN(\"d\") - NUMTODSINTERVAL(7, 'DAY'))", dialect.DateSubtractDays("MIN(\"d\")", 7));
        Assert.Equal("HEXTORAW('DEAD')", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    [Fact]
    public void SqlServerDialect_Brackets_DateAdd_HexLiteral()
    {
        var dialect = new SqlServerSourceDialect();
        Assert.Equal("[o]]dd]", dialect.QuoteIdentifier("o]dd"));
        Assert.Equal("[erp].[orders]", dialect.QualifyObject(Table));
        Assert.Equal("DATEADD(day, -7, MIN([d]))", dialect.DateSubtractDays("MIN([d])", 7));
        Assert.Equal("0xDEAD", dialect.FormatBinaryLiteral([0xDE, 0xAD]));
    }

    [Fact]
    public void TemporalLiterals_QuotedForMostDialects_ExplicitConversionForOracle()
    {
        const string ts = "2024-01-03 10:00:00.000";

        // SQL Server, MySQL, PostgreSQL accept the ISO string directly.
        Assert.Equal($"'{ts}'", new SqlServerSourceDialect().FormatTemporalLiteral("datetime2", ts));
        Assert.Equal($"'{ts}'", new MySqlSourceDialect().FormatTemporalLiteral("datetime2", ts));
        Assert.Equal($"'{ts}'", new PostgresSourceDialect().FormatTemporalLiteral("datetime2", ts));

        // Oracle wraps in an explicit conversion so the comparison never depends on the session NLS format.
        var oracle = new OracleSourceDialect();
        Assert.Equal("TO_DATE('2024-01-03', 'YYYY-MM-DD')", oracle.FormatTemporalLiteral("date", "2024-01-03"));
        Assert.Equal($"TO_TIMESTAMP('{ts}', 'YYYY-MM-DD HH24:MI:SS.FF3')", oracle.FormatTemporalLiteral("datetime2", ts));
        Assert.Equal($"TO_TIMESTAMP('{ts}', 'YYYY-MM-DD HH24:MI:SS.FF3')", oracle.FormatTemporalLiteral("datetime", ts));
        Assert.Equal("TO_TIMESTAMP_TZ('2024-01-03 10:00:00.000 +02:00', 'YYYY-MM-DD HH24:MI:SS.FF3 TZH:TZM')",
            oracle.FormatTemporalLiteral("datetimeoffset", "2024-01-03 10:00:00.000 +02:00"));
    }

    // ---- Canonicalizers (builder parsing is local, no live database) ----

    [Fact]
    public void MySqlCanonicalizer_TrustedPasses_AndRedacts()
    {
        var canonical = new MySqlConnectionStringCanonicalizer()
            .Canonicalize("Server=db;Database=erp;User ID=u;Password=p", ConnectionRole.Source, SecretlessPolicy.Trusted);
        Assert.Contains("Password", canonical.Canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=p", canonical.Redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLFlow Source", canonical.Canonical, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=db;Database=erp;User ID=u;Password=p")]
    [InlineData("Server=db;Database=erp;User ID=u;Password=p;GuidFormat=Char36")]
    [InlineData("Server=db;Database=erp;User ID=u;Password=p;OldGuids=True")]
    public void MySqlCanonicalizer_ReadsChar36AsText(string connectionString)
    {
        // A CHAR(36) column must arrive as the nchar the type mapper declared for it, not as a CLR Guid the
        // bulk copy into that column would reject. The engine owns the mapping, so a caller's GuidFormat or
        // the deprecated OldGuids never reinstates the reinterpretation.
        var canonical = new MySqlConnectionStringCanonicalizer()
            .Canonicalize(connectionString, ConnectionRole.Source, SecretlessPolicy.Trusted);

        var settings = new MySqlConnectionStringBuilder(canonical.Canonical);
        Assert.Equal(MySqlGuidFormat.None, settings.GuidFormat);
        Assert.False(settings.OldGuids);
    }

    [Fact]
    public void MySqlCanonicalizer_InlineLiteral_RejectedClearly()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new MySqlConnectionStringCanonicalizer()
            .Canonicalize("Server=db;User ID=u;Password=p", ConnectionRole.Source, SecretlessPolicy.RequireSelfAuthenticating));
        Assert.Contains("${...}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresCanonicalizer_TrustedPasses_InlineRejected()
    {
        var canonicalizer = new PostgresConnectionStringCanonicalizer();
        var canonical = canonicalizer.Canonicalize("Host=db;Database=erp;Username=u;Password=p", ConnectionRole.Source, SecretlessPolicy.Trusted);
        Assert.DoesNotContain("Password=p", canonical.Redacted, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<SqlFlowException>(() => canonicalizer
            .Canonicalize("Host=db;Username=u;Password=p", ConnectionRole.Source, SecretlessPolicy.RequireSelfAuthenticating));
    }

    [Fact]
    public void OracleCanonicalizer_TrustedPasses_AndRedacts_InlineRejected()
    {
        var canonicalizer = new OracleConnectionStringCanonicalizer();
        var canonical = canonicalizer.Canonicalize(
            "User Id=scott;Password=secret123;Data Source=localhost:1521/FREEPDB1", ConnectionRole.Source, SecretlessPolicy.Trusted);
        Assert.DoesNotContain("secret123", canonical.Redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scott", canonical.Redacted, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<SqlFlowException>(() => canonicalizer
            .Canonicalize("User Id=scott;Password=secret123;Data Source=x", ConnectionRole.Source, SecretlessPolicy.RequireSelfAuthenticating));
    }

    // ---- Composite registries: a closed set with clear errors, never a silent null ----

    [Fact]
    public async Task CompositeConnectionFactory_UnknownKind_FailsClearly()
    {
        var composite = new CompositeConnectionFactory([new SqlConnectionFactory()]);
        var resolved = Resolved(DataSourceKind.PostgreSQL);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => composite.OpenAsync(resolved));
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SqlFlow.Providers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeCatalogReaderFactory_DispatchesByKind()
    {
        var composite = new CompositeCatalogReaderFactory([new MySqlCatalogReader(), new PostgresCatalogReader(), new OracleCatalogReader()]);
        Assert.IsType<MySqlCatalogReader>(composite.ReaderFor(Resolved(DataSourceKind.MySQL)));
        Assert.IsType<PostgresCatalogReader>(composite.ReaderFor(Resolved(DataSourceKind.PostgreSQL)));
        Assert.IsType<OracleCatalogReader>(composite.ReaderFor(Resolved(DataSourceKind.Oracle)));
        Assert.Throws<SqlFlowException>(() => composite.ReaderFor(Resolved(DataSourceKind.MSSQL)));
    }

    private static ResolvedConnection Resolved(DataSourceKind kind) => new()
    {
        Kind = kind,
        CanonicalString = "Server=x;Database=y;",
        RedactedString = "Server=x;Database=y;",
    };
}
