using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.Postgres;

/// <summary>
/// Column-level introspection behavior of the PostgreSQL catalog reader against a live database: nullability,
/// identity, primary-key membership, default expressions, ordinal order, views, the not-found contract, and
/// quoted / mixed-case / reserved-word identifier fidelity. These drive <c>IntrospectObjectAsync</c> against the
/// <c>public</c> schema on the connected database. Unquoted identifiers fold to lower case, so plain names are
/// stored and referenced in lower case. Gated on <c>SQLFLOW_TEST_PG</c> only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresCatalogBehaviorTests
{
    private const DataSourceKind Kind = DataSourceKind.PostgreSQL;
    private const string Schema = "public";

    [SkippableFact]
    public async Task Nullability_IsReported()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_null");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection, $"CREATE TABLE public.{table} (a integer NOT NULL, b integer)");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            Assert.False(Column(obj, "a").IsNullable);
            Assert.True(Column(obj, "b").IsNullable);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Identity_And_PrimaryKey_AreReported()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_id");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection,
            $"CREATE TABLE public.{table} (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name varchar(20))");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            var id = Column(obj, "id");
            Assert.True(id.IsIdentity);
            Assert.True(id.IsPrimaryKeyMember);
            Assert.False(id.IsNullable);
            Assert.False(Column(obj, "name").IsPrimaryKeyMember);
            Assert.False(Column(obj, "name").IsIdentity);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Serial_IsReportedAsIdentity()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_serial");
        await PostgresTestSupport.DropTableAsync(connection, table);
        // Legacy serial columns carry a nextval default rather than a declared identity; the reader flags both.
        await PostgresTestSupport.ExecAsync(connection, $"CREATE TABLE public.{table} (id serial PRIMARY KEY, name varchar(20))");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            var id = Column(obj, "id");
            Assert.True(id.IsIdentity);
            Assert.True(id.IsPrimaryKeyMember);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task CompositePrimaryKey_MarksEveryMember()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_cpk");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection,
            $"CREATE TABLE public.{table} (a integer, b integer, c integer, CONSTRAINT {table}_pk PRIMARY KEY (a, b))");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            Assert.True(Column(obj, "a").IsPrimaryKeyMember);
            Assert.True(Column(obj, "b").IsPrimaryKeyMember);
            Assert.False(Column(obj, "c").IsPrimaryKeyMember);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task DefaultExpression_IsCaptured()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_def");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection,
            $"CREATE TABLE public.{table} (status varchar(10) DEFAULT 'NEW', n integer)");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            Assert.Contains("NEW", Column(obj, "status").DefaultExpression ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(Column(obj, "n").DefaultExpression);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Ordinals_FollowColumnOrder()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_ord");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection, $"CREATE TABLE public.{table} (first integer, second integer, third integer)");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            Assert.Equal(new[] { "first", "second", "third" }, obj!.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name));
            Assert.Equal(1, Column(obj, "first").Ordinal);
            Assert.Equal(3, Column(obj, "third").Ordinal);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task View_IntrospectsAsView()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_vt");
        var view = PostgresTestSupport.Unique("sf_vv");
        await PostgresTestSupport.ExecAsync(connection, $"DROP VIEW IF EXISTS public.\"{view}\"");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection, $"CREATE TABLE public.{table} (id integer, label varchar(20))");
        await PostgresTestSupport.ExecAsync(connection, $"CREATE VIEW public.{view} AS SELECT id, label FROM public.{table}");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, view);
            Assert.NotNull(obj);
            Assert.Equal(ObjectType.View, obj!.Type);
            Assert.Equal(2, obj.Columns.Count);
        }
        finally
        {
            await PostgresTestSupport.ExecAsync(connection, $"DROP VIEW IF EXISTS public.\"{view}\"");
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task MissingObject_ReturnsNull()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, PostgresTestSupport.Unique("sf_missing"));
        Assert.Null(obj);
    }

    [SkippableFact]
    public async Task QuotedMixedCaseAndReservedIdentifiers_RoundTrip()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var table = PostgresTestSupport.Unique("sf_quoted");
        await PostgresTestSupport.DropTableAsync(connection, table);
        // Quoted identifiers preserve case and allow spaces and reserved words that unquoted names cannot.
        await PostgresTestSupport.ExecAsync(connection,
            $"CREATE TABLE public.{table} (\"MixedCase\" integer, \"with space\" integer, \"select\" integer)");
        try
        {
            var obj = await PostgresTestSupport.IntrospectAsync(connection, Schema, table);
            Assert.NotNull(obj);
            var names = obj!.Columns.Select(c => c.Name).ToList();
            Assert.Contains("MixedCase", names);
            Assert.Contains("with space", names);
            Assert.Contains("select", names);
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
        }
    }

    private static CatalogColumn Column(CatalogObject? obj, string name)
        => obj!.Columns.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
