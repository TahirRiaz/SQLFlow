using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Integration.MySql;

/// <summary>
/// Column-level introspection behavior of the MySQL catalog reader against a live database: nullability,
/// identity (AUTO_INCREMENT), primary-key membership, default expressions, ordinal order, views, the not-found
/// contract, and backtick-quoted / mixed-case / reserved-word identifier fidelity. These drive
/// <c>IntrospectObjectAsync</c> in the connection's current database (a schema IS a database here), so they
/// run against whatever database <c>SQLFLOW_TEST_MYSQL</c> names. Gated on <c>SQLFLOW_TEST_MYSQL</c> only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlCatalogBehaviorTests
{
    private const DataSourceKind Kind = DataSourceKind.MySQL;

    [SkippableFact]
    public async Task Nullability_IsReported()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_null");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection, $"CREATE TABLE `{table}` (a INT NOT NULL, b INT)");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.False(Column(obj, "a").IsNullable);
            Assert.True(Column(obj, "b").IsNullable);
        }
        finally
        {
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Identity_And_PrimaryKey_AreReported()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_id");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection,
            $"CREATE TABLE `{table}` (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, name VARCHAR(20))");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, table);
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
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task CompositePrimaryKey_MarksEveryMember()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_cpk");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection,
            $"CREATE TABLE `{table}` (a INT NOT NULL, b INT NOT NULL, c INT, PRIMARY KEY (a, b))");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.True(Column(obj, "a").IsPrimaryKeyMember);
            Assert.True(Column(obj, "b").IsPrimaryKeyMember);
            Assert.False(Column(obj, "c").IsPrimaryKeyMember);
        }
        finally
        {
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task DefaultExpression_IsCaptured()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_def");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection,
            $"CREATE TABLE `{table}` (status VARCHAR(10) DEFAULT 'NEW', n INT)");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.Contains("NEW", Column(obj, "status").DefaultExpression ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(Column(obj, "n").DefaultExpression);
        }
        finally
        {
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task Ordinals_FollowColumnOrder()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_ord");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection, $"CREATE TABLE `{table}` (first INT, second INT, third INT)");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            Assert.Equal(new[] { "first", "second", "third" }, obj!.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name.ToLowerInvariant()));
            Assert.Equal(1, Column(obj, "first").Ordinal);
            Assert.Equal(3, Column(obj, "third").Ordinal);
        }
        finally
        {
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task View_IntrospectsAsView()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_vt");
        var view = MySqlTestSupport.Unique("sf_vv");
        await MySqlTestSupport.ExecAsync(connection, $"DROP VIEW IF EXISTS `{view}`");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection, $"CREATE TABLE `{table}` (id INT, label VARCHAR(20))");
        await MySqlTestSupport.ExecAsync(connection, $"CREATE VIEW `{view}` AS SELECT id, label FROM `{table}`");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, view);
            Assert.NotNull(obj);
            Assert.Equal(ObjectType.View, obj!.Type);
            Assert.Equal(2, obj.Columns.Count);
        }
        finally
        {
            await MySqlTestSupport.ExecAsync(connection, $"DROP VIEW IF EXISTS `{view}`");
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    [SkippableFact]
    public async Task MissingObject_ReturnsNull()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, MySqlTestSupport.Unique("sf_missing"));
        Assert.Null(obj);
    }

    [SkippableFact]
    public async Task QuotedMixedCaseAndReservedIdentifiers_RoundTrip()
    {
        var cs = ForeignDb.Require(Kind);
        await using var connection = await ForeignDb.OpenAsync(Kind, cs);
        var schema = await MySqlTestSupport.CurrentDatabaseAsync(connection);
        var table = MySqlTestSupport.Unique("sf_quoted");
        await MySqlTestSupport.DropTableAsync(connection, table);
        // MySQL preserves column-name case regardless of lower_case_table_names, and backtick-quoted names
        // may contain spaces and reserved words that unquoted names cannot.
        await MySqlTestSupport.ExecAsync(connection,
            $"CREATE TABLE `{table}` (`Mixed Case` INT, `with space` INT, `select` INT)");
        try
        {
            var obj = await MySqlTestSupport.IntrospectAsync(connection, schema, table);
            Assert.NotNull(obj);
            var names = obj!.Columns.Select(c => c.Name).ToList();
            Assert.Contains("Mixed Case", names);
            Assert.Contains("with space", names);
            Assert.Contains("select", names);
        }
        finally
        {
            await MySqlTestSupport.DropTableAsync(connection, table);
        }
    }

    private static CatalogColumn Column(CatalogObject? obj, string name)
        => obj!.Columns.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
