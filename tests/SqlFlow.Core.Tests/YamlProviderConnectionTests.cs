using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The provider syntax of the YAML connections block: plain string stays SQL Server (back-compat),
/// the map form selects MySQL/PostgreSQL, the target must remain SQL Server, and the two-part object form
/// doubles the database for MySQL.</summary>
public sealed class YamlProviderConnectionTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    [Fact]
    public void MapForm_SelectsProvider_StringFormStaysMssql()
    {
        var doc = Loader.Parse("""
            flowType: ing
            connections:
              erp:
                provider: postgres
                connection: ${env:ERP_PG}
              shop:
                provider: mysql
                connection: ${env:SHOP_MY}
              dwh: ${env:DWH}
            source:
              server: erp
              object: erp.public.orders
            target:
              server: dwh
              object: dw.raw.orders
            """);

        Assert.Equal(DataSourceKind.PostgreSQL, doc.Connections.Single(c => c.Alias == "erp").Kind);
        Assert.Equal(DataSourceKind.MySQL, doc.Connections.Single(c => c.Alias == "shop").Kind);
        Assert.Equal(DataSourceKind.MSSQL, doc.Connections.Single(c => c.Alias == "dwh").Kind);
    }

    [Fact]
    public void DirectConnection_WithProvider_SynthesizesForeignSource()
    {
        var doc = Loader.Parse("""
            flowType: ing
            source:
              provider: mysql
              connection: ${env:SHOP_MY}
              object: shop.orders
            target:
              connection: ${env:DWH}
              object: dw.raw.orders
            """);

        Assert.Equal(DataSourceKind.MySQL, doc.Connections.Single(c => c.Alias == "source").Kind);
        Assert.Equal(DataSourceKind.MSSQL, doc.Connections.Single(c => c.Alias == "target").Kind);

        // The two-part object doubled the database into the schema slot (MySQL schema IS the database).
        Assert.Equal("shop", doc.Flow.Source.Table.Database);
        Assert.Equal("shop", doc.Flow.Source.Table.Schema);
        Assert.Equal("orders", doc.Flow.Source.Table.Name);
    }

    [Fact]
    public void ForeignTarget_RejectedAtParseTime()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            connections:
              erp:
                provider: postgres
                connection: ${env:ERP_PG}
              dwh: ${env:DWH}
            source:
              server: dwh
              object: db.dbo.t
            target:
              server: erp
              object: erp.public.t
            """));
        Assert.Contains("must be SQL Server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProvider_FailsWithAllowedList()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            connections:
              x:
                provider: db2
                connection: y
            source: { server: x, object: a.b.c }
            target: { connection: z, object: a.b.c }
            """));
        Assert.Contains("db2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mssql, azdb, mysql, postgres", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapForm_WithoutConnection_TakesTheCanonicalConvention_KeepingTheProvider()
    {
        // The canonical secrets pattern: a connection without a reference resolves its well-known
        // SQLFLOW_CONN_* variable; the declared provider is preserved.
        var doc = Loader.Parse("""
            flowType: ing
            connections:
              x:
                provider: mysql
            source: { server: x, object: a.b }
            target: { connection: z, object: a.b.c }
            """);

        var x = doc.Connections.Single(c => c.Alias == "x");
        Assert.Equal("${env:SQLFLOW_CONN_X}", x.ConnectionRef);
        Assert.Equal(DataSourceKind.MySQL, x.Kind);
    }
}
