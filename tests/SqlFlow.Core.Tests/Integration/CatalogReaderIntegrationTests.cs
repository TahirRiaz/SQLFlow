using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Catalog;
using SqlFlow.SqlServer.Catalog;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the SQL Server catalog reader (list objects, list databases, search, full introspection)
/// against the real sink database. Each test drops its own table first and at the end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogReaderIntegrationTests
{
    private static readonly SqlServerCatalogReader Reader = new();

    private static async Task<DbConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    [SkippableFact]
    public async Task ListObjects_SeesACreatedTable()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfCat_List";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL);");

        try
        {
            await using var connection = await OpenAsync(cs);
            var page = await Reader.ListObjectsAsync(connection, new ObjectScope { Schema = "dbo" }, new CatalogQuery { NameLike = "_SfCat_List" });

            Assert.Contains(page.Items, o => o.Name == table && o.Type == ObjectType.Table);
            Assert.True(page.Total >= 1);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Introspect_ReturnsColumnTypesAndPrimaryKey()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfCat_Introspect";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [dbo].[{table}] (
                [Id] int NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY CLUSTERED,
                [Name] nvarchar(50) NULL,
                [Amount] decimal(18,2) NULL
            );
            """);

        try
        {
            await using var connection = await OpenAsync(cs);
            var obj = await Reader.IntrospectObjectAsync(connection, new ThreePartName { Schema = "dbo", Name = table });

            Assert.NotNull(obj);
            Assert.Equal(ObjectType.Table, obj!.Type);

            Assert.Equal("int", obj.Columns.Single(c => c.Name == "Id").NativeType);
            Assert.Equal("nvarchar(50)", obj.Columns.Single(c => c.Name == "Name").NativeType);
            Assert.Equal("decimal(18, 2)", obj.Columns.Single(c => c.Name == "Amount").NativeType);

            var id = obj.Columns.Single(c => c.Name == "Id");
            Assert.False(id.IsNullable);
            Assert.True(id.IsPrimaryKeyMember);

            Assert.Contains(obj.Indexes, i => i.IsPrimaryKey && i.KeyColumns.SequenceEqual(new[] { "Id" }));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Introspect_View_ReturnsItsDefinition()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfCat_DefBase";
        const string view = "_SfCat_DefView";
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(
            cs, $"EXEC('CREATE VIEW [dbo].[{view}] AS SELECT [Id], [Name] FROM [dbo].[{table}] WHERE [Id] > 0;');");

        try
        {
            await using var connection = await OpenAsync(cs);
            var obj = await Reader.IntrospectObjectAsync(connection, new ThreePartName { Schema = "dbo", Name = view });

            Assert.NotNull(obj);
            Assert.Equal(ObjectType.View, obj!.Type);
            Assert.Equal(DefinitionAvailability.Available, obj.DefinitionAvailability);
            Assert.NotNull(obj.Definition);
            // The engine stores the module text verbatim, so the body the view was created with comes back.
            Assert.Contains($"CREATE VIEW [dbo].[{view}]", obj.Definition, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[Id] > 0", obj.Definition, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Introspect_Table_ReportsNoDefinitionToRead()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfCat_DefTable";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL);");

        try
        {
            await using var connection = await OpenAsync(cs);
            var obj = await Reader.IntrospectObjectAsync(connection, new ThreePartName { Schema = "dbo", Name = table });

            Assert.NotNull(obj);
            // A table is not a view that failed to yield its source: the distinction is what stops the GUI
            // showing "source unavailable" against every table in the browser.
            Assert.Equal(DefinitionAvailability.NotApplicable, obj!.DefinitionAvailability);
            Assert.Null(obj.Definition);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Introspect_MissingObject_ReturnsNull()
    {
        var cs = IntegrationDb.Require();
        await using var connection = await OpenAsync(cs);
        var name = new ThreePartName { Schema = "dbo", Name = "_SfCat_Missing_" + Guid.NewGuid().ToString("N") };
        Assert.Null(await Reader.IntrospectObjectAsync(connection, name));
    }

    [SkippableFact]
    public async Task ListDatabases_IncludesCurrentDatabase()
    {
        var cs = IntegrationDb.Require();
        await using var connection = await OpenAsync(cs);
        var current = connection.Database;

        var databases = await Reader.ListDatabasesAsync(connection, new CatalogQuery { IncludeSystem = true, NameLike = current });

        Assert.Contains(databases, d => string.Equals(d.Name, current, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Search_FindsTableByTerm()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfCat_SearchMe";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL);");

        try
        {
            await using var connection = await OpenAsync(cs);
            var matches = await Reader.SearchObjectsAsync(connection, database: null, term: "_SfCat_SearchMe");
            Assert.Contains(matches, m => m.Name == table);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }
}
