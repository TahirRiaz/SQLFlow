using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end XML ingestion against the physical sink, proving the path-based XML flattener rides the same
/// engine path as CSV/XLS/JSON: rows are flattened (attributes + nested elements) to raw string columns,
/// repeating elements explode into rows, schema evolves across versions, and everything is bulk-loaded with
/// the provenance columns. Each test drops its table up front.
/// </summary>
[Trait("Category", "Integration")]
public sealed class XmlIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_xmlit_" + Guid.NewGuid().ToString("N"));

    public XmlIntegrationTests() => Directory.CreateDirectory(_dir);

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static FlowDefinition Flow(string table, string connection, string location, Dictionary<string, string?>? options = null, LoadMode mode = LoadMode.TruncateLoad)
        => new()
        {
            Name = table,
            Source = new SourceSpec { Type = "xml", Location = location, Options = options ?? new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = connection, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen, DefaultColumnType = "nvarchar(4000)" },
            Load = new LoadPolicy { Mode = mode },
        };

    [SkippableFact]
    public async Task Xml_FlattensAttributesAndNestedElementsIntoSink()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Xml_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = Write("orders.xml",
            """
            <orders>
              <order id="1"><customer><name>Ann</name></customer><total>9.50</total></order>
              <order id="2"><customer><name>Bob</name></customer><total>3.00</total></order>
            </orders>
            """);

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "customer_name"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "id"));        // the @id attribute
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "FileName_DW"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [customer_name] FROM [dbo].[{table}] WHERE [id] = '2'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Xml_ExplodesRepeatingElementsIntoRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_XmlExplode_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = Write("po.xml",
            "<orders><order><id>1</id><line><sku>A</sku><qty>2</qty></line><line><sku>B</sku><qty>5</qty></line></order></orders>");
        var options = new Dictionary<string, string?> { ["explodePaths"] = "/line" };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, path, options));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "line_sku"));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "line"));
            Assert.Equal("2", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [line_qty] FROM [dbo].[{table}] WHERE [line_sku] = 'A'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Xml_SchemaEvolution_AliasReconcilesRenamedElement()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_XmlEvolve_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        Write("v1.xml", "<rows><row><id>1</id><name>Ann</name></row></rows>");
        Write("v2.xml", "<rows><row><id>2</id><fullName>Bob</fullName></row></rows>");
        var options = new Dictionary<string, string?>
        {
            ["srcFile"] = "*.xml",
            ["pathAliases"] = "person_name=/name|/fullName",
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(table, cs, _dir, options));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(2, result.RowsLoaded);
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "person_name"));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "fullName"));
            Assert.Equal("Ann", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [person_name] FROM [dbo].[{table}] WHERE [id] = '1'"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [person_name] FROM [dbo].[{table}] WHERE [id] = '2'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
