using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The scaffolder's contract: its output is a RUNNABLE flow document (it round-trips through the
/// real YAML loader), keys come from the introspected primary key, the provider forms are correct, and no
/// secret can be embedded.</summary>
public sealed class CatalogScaffolderTests
{
    private static CatalogObject Orders(string schema = "sales") => new()
    {
        Name = new ThreePartName { Schema = schema, Name = "orders" },
        Type = ObjectType.Table,
        Columns =
        [
            new CatalogColumn { Name = "id", Ordinal = 1, NativeType = "int", IsNullable = false, IsPrimaryKeyMember = true },
            new CatalogColumn { Name = "amount", Ordinal = 2, NativeType = "decimal(10, 2)", IsNullable = false },
            new CatalogColumn { Name = "updated_at", Ordinal = 3, NativeType = "datetime2(3)", IsNullable = false },
        ],
    };

    [Fact]
    public void Scaffold_RoundTripsThroughTheLoader_AsRunnableDocument()
    {
        var yaml = CatalogScaffolder.ToIngestionYaml(Orders(), new ScaffoldOptions
        {
            SourceConnection = "${env:SQLFLOW_SRC}",
            TargetConnection = "${env:SQLFLOW_DW}",
            TargetObject = "raw.orders",
        });

        var document = new YamlIngestionFlowLoader().Parse(yaml);

        Assert.Equal("sales-orders", document.Flow.SysAlias);
        Assert.Equal(["id"], document.Flow.Load.KeyColumns);            // PK auto-detected
        Assert.Equal("orders", document.Flow.Source.Table.Name);
        Assert.Equal("sales", document.Flow.Source.Table.Schema);       // 2-part object doubled
        Assert.Equal("raw", document.Flow.Target.Table.Schema);
        Assert.Equal(2, document.Connections.Count);
        Assert.All(document.Connections, c => Assert.StartsWith("${env:", c.ConnectionRef, StringComparison.Ordinal));
    }

    [Fact]
    public void Scaffold_MySqlProvider_EmitsMapForm_AndParses()
    {
        var yaml = CatalogScaffolder.ToIngestionYaml(Orders(schema: "shop"), new ScaffoldOptions
        {
            SourceConnection = "${env:SHOP_MYSQL}",
            SourceProvider = "mysql",
            TargetConnection = "${env:SQLFLOW_DW}",
            TargetObject = "raw.orders",
        });

        var document = new YamlIngestionFlowLoader().Parse(yaml);
        Assert.Equal(DataSourceKind.MySQL, document.Connections.Single(c => c.Alias == "src").Kind);
        Assert.Equal("shop", document.Flow.Source.Table.Schema);        // the MySQL database
    }

    [Fact]
    public void Scaffold_SuggestsIncrementalCandidates_AsComments()
    {
        var yaml = CatalogScaffolder.ToIngestionYaml(Orders(), new ScaffoldOptions
        {
            SourceConnection = "${env:S}",
            TargetConnection = "${env:T}",
            TargetObject = "raw.orders",
        });

        Assert.Contains("# incremental:", yaml, StringComparison.Ordinal);
        Assert.Contains("#   columns: [updated_at]", yaml, StringComparison.Ordinal);
        Assert.Contains("[PK]", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_NoPrimaryKey_WarnsInsteadOfGuessing()
    {
        var noPk = Orders() with
        {
            Columns = [new CatalogColumn { Name = "val", Ordinal = 1, NativeType = "int" }],
        };

        var yaml = CatalogScaffolder.ToIngestionYaml(noPk, new ScaffoldOptions
        {
            SourceConnection = "${env:S}",
            TargetConnection = "${env:T}",
            TargetObject = "raw.orders",
        });

        Assert.Contains("keyColumns: []", yaml, StringComparison.Ordinal);
        Assert.Contains("no primary key detected", yaml, StringComparison.Ordinal);

        // Still parses (a keyless flow is append-only).
        var document = new YamlIngestionFlowLoader().Parse(yaml);
        Assert.Empty(document.Flow.Load.KeyColumns);
    }

    [Fact]
    public void Scaffold_ExplicitKeys_OverridePk()
    {
        var yaml = CatalogScaffolder.ToIngestionYaml(Orders(), new ScaffoldOptions
        {
            SourceConnection = "${env:S}",
            TargetConnection = "${env:T}",
            TargetObject = "raw.orders",
            KeyColumns = ["amount"],
        });

        Assert.Contains("keyColumns: [amount]", yaml, StringComparison.Ordinal);
    }
}
