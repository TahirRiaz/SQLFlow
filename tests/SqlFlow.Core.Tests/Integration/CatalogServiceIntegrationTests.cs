using Microsoft.Data.SqlClient;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Catalog;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Drives source discovery through the full <see cref="CatalogService"/> path (resolve a reference, open via
/// the connection factory, pick the reader, read) against the real sink. The reference is a whole
/// <c>${env:...}</c> secret reference, so the resolver's secretless gate trusts it (the sink uses SQL auth).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogServiceIntegrationTests
{
    private const string Reference = "${env:SQLFlowSinkConStr}";

    private static CatalogService Service()
    {
        var resolver = new ConnectionResolver(
            new NullDataSourceStore(),
            new SecretResolver([new EnvSecretProvider()]),
            [new SqlConnectionStringCanonicalizer()]);
        return new CatalogService(resolver, new SqlConnectionFactory(), new SqlServerCatalogReaderFactory());
    }

    [SkippableFact]
    public async Task Objects_Introspect_Scaffold_ThroughTheService()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfSvc_Discover";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, [Name] nvarchar(50) NULL);");

        try
        {
            var service = Service();

            var page = await service.ObjectsAsync(Reference, new ObjectScope { Schema = "dbo" }, new CatalogQuery { NameLike = "_SfSvc_Discover" });
            Assert.Contains(page.Items, o => o.Name == table);

            var obj = await service.IntrospectAsync(Reference, new ThreePartName { Schema = "dbo", Name = table });
            Assert.NotNull(obj);
            Assert.Equal("nvarchar(50)", obj!.Columns.Single(c => c.Name == "Name").NativeType);

            var yaml = CatalogScaffolder.ToIngestionYaml(obj, new ScaffoldOptions
            {
                SourceConnection = "${env:SQLFLOW_SRC}",
                TargetConnection = "${env:SQLFLOW_DW}",
                TargetObject = $"stg.{table}",
            });

            Assert.Contains("flowType: ing", yaml, StringComparison.Ordinal);
            Assert.Contains("keyColumns: [Id]", yaml, StringComparison.Ordinal);    // PK auto-detected
            Assert.Contains("Name : nvarchar(50) : NULL", yaml, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Databases_ThroughTheService_IncludeCurrent()
    {
        var cs = IntegrationDb.Require();
        await using var probe = new SqlConnection(cs);
        await probe.OpenAsync();
        var current = probe.Database;

        var databases = await Service().DatabasesAsync(Reference, new CatalogQuery { IncludeSystem = true, NameLike = current });

        Assert.Contains(databases, d => string.Equals(d.Name, current, StringComparison.OrdinalIgnoreCase));
    }
}
