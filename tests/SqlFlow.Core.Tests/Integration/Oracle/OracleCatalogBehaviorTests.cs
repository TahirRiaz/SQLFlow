using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Oracle;

/// <summary>
/// Column-level introspection behavior of the Oracle catalog reader against a live database: nullability,
/// identity, primary-key membership, default expressions, ordinal order, views, the not-found contract, and
/// quoted / mixed-case / reserved-word identifier fidelity. These drive <c>IntrospectObjectAsync</c> in the
/// connection's current schema, so they run under whatever principal <c>SQLFLOW_TEST_ORACLE</c> names.
/// Gated on <c>SQLFLOW_TEST_ORACLE</c> only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OracleCatalogBehaviorTests
{
    private const DataSourceKind Kind = DataSourceKind.Oracle;

    [SkippableFact]
    public async Task Nullability_IsReported()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_NULL");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection, $"CREATE TABLE {table} (a NUMBER NOT NULL, b NUMBER)");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.False(Column(obj, "A").IsNullable);
            Assert.True(Column(obj, "B").IsNullable);
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Identity_And_PrimaryKey_AreReported()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_ID");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection,
            $"CREATE TABLE {table} (id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name VARCHAR2(20))");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            var id = Column(obj, "ID");
            Assert.True(id.IsIdentity);
            Assert.True(id.IsPrimaryKeyMember);
            Assert.False(id.IsNullable);
            Assert.False(Column(obj, "NAME").IsPrimaryKeyMember);
            Assert.False(Column(obj, "NAME").IsIdentity);
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task CompositePrimaryKey_MarksEveryMember()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_CPK");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection,
            $"CREATE TABLE {table} (a NUMBER, b NUMBER, c NUMBER, CONSTRAINT {table}_pk PRIMARY KEY (a, b))");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.True(Column(obj, "A").IsPrimaryKeyMember);
            Assert.True(Column(obj, "B").IsPrimaryKeyMember);
            Assert.False(Column(obj, "C").IsPrimaryKeyMember);
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task DefaultExpression_IsCaptured()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_DEF");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection,
            $"CREATE TABLE {table} (status VARCHAR2(10) DEFAULT 'NEW', n NUMBER)");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.Contains("NEW", Column(obj, "STATUS").DefaultExpression ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(Column(obj, "N").DefaultExpression);
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Ordinals_FollowColumnOrder()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_ORD");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection, $"CREATE TABLE {table} (first NUMBER, second NUMBER, third NUMBER)");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.Equal(new[] { "FIRST", "SECOND", "THIRD" }, obj!.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name));
            Assert.Equal(1, Column(obj, "FIRST").Ordinal);
            Assert.Equal(3, Column(obj, "THIRD").Ordinal);
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task View_IntrospectsAsView()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_VT");
        var view = OracleTestSupport.Unique("SF_VV");
        await OracleTestSupport.ExecAsync(connection,
            $"BEGIN EXECUTE IMMEDIATE 'DROP VIEW {view}'; EXCEPTION WHEN OTHERS THEN NULL; END;");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection, $"CREATE TABLE {table} (id NUMBER, label VARCHAR2(20))");
        await OracleTestSupport.ExecAsync(connection, $"CREATE VIEW {view} AS SELECT id, label FROM {table}");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, view);
            Assert.NotNull(obj);
            Assert.Equal(ObjectType.View, obj!.Type);
            Assert.Equal(2, obj.Columns.Count);
        }
        finally
        {
            await OracleTestSupport.ExecAsync(connection,
                $"BEGIN EXECUTE IMMEDIATE 'DROP VIEW {view}'; EXCEPTION WHEN OTHERS THEN NULL; END;");
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task MissingObject_ReturnsNull()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var obj = await OracleTestSupport.IntrospectAsync(connection, schema, OracleTestSupport.Unique("SF_MISSING"));
        Assert.Null(obj);
    }

    [SkippableFact]
    public async Task QuotedMixedCaseAndReservedIdentifiers_RoundTrip()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_QUOTED");
        await OracleTestSupport.DropTableAsync(connection, table);
        // Quoted identifiers preserve case and allow spaces and reserved words that unquoted names cannot.
        await OracleTestSupport.ExecAsync(connection,
            $"CREATE TABLE {table} (\"MixedCase\" NUMBER, \"with space\" NUMBER, \"SELECT\" NUMBER)");
        try
        {
            var obj = await OracleTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            var names = obj!.Columns.Select(c => c.Name).ToList();
            Assert.Contains("MixedCase", names);
            Assert.Contains("with space", names);
            Assert.Contains("SELECT", names);
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
        }
    }

    private static CatalogColumn Column(CatalogObject? obj, string name)
        => obj!.Columns.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
