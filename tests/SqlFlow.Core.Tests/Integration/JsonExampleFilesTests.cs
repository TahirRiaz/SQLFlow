using SqlFlow.Core.Model;
using SqlFlow.Sources;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the JSON reader against the real example files under <c>data/jsn</c> (a flat customer array,
/// a product array with a nested vendor object and a details array of objects). The inventory test needs
/// no database; the load test writes a physical table. Both skip cleanly when the files are absent.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JsonExampleFilesTests
{
    [SkippableFact]
    public async Task Inventory_OnProductJson_SurfacesContainersAndArrayElementFields()
    {
        var file = Locate("data", "jsn", "product.json");
        Skip.If(file is null, "data/jsn/product.json not found.");

        var reader = new JsonSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);
        var source = new SourceSpec { Type = "json", Location = file, Options = new Dictionary<string, string?>() };

        var inventory = await reader.InventoryAsync(source, maxFiles: 1, maxRecords: 20, maxDepth: 20);
        var kinds = inventory.Paths.ToDictionary(p => p.Path, p => p.Kind, StringComparer.Ordinal);

        Assert.Equal(JsonNodeKind.Object, kinds["$.vendor"]);
        Assert.Equal(JsonNodeKind.Value, kinds["$.vendor.name"]);
        Assert.Equal(JsonNodeKind.Array, kinds["$.details"]);
        Assert.Equal(JsonNodeKind.Object, kinds["$.details[*]"]);
        Assert.Equal(JsonNodeKind.Value, kinds["$.details[*].track_id"]);
    }

    [SkippableFact]
    public async Task Load_ProductJson_FlattensVendorAndKeepsDetailsArrayAsJson()
    {
        var cs = IntegrationDb.Require();
        var file = Locate("data", "jsn", "product.json");
        Skip.If(file is null, "data/jsn/product.json not found.");

        var table = "IT_JsonProduct_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var flow = new FlowDefinition
        {
            Name = table,
            Source = new SourceSpec { Type = "json", Location = file!, Options = new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen, DefaultColumnType = "nvarchar(max)" },
            Load = new LoadPolicy { Mode = LoadMode.TruncateLoad },
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.True(result.RowsLoaded > 0);
            Assert.Equal(result.RowsLoaded, await IntegrationDb.RowCountAsync(cs, table));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "vendor_name"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "details"));
            Assert.Equal("AC/DC", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [vendor_name] FROM [dbo].[{table}] WHERE [id] = '1'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Explode_ProductDetails_YieldsOneRowPerTrack()
    {
        var cs = IntegrationDb.Require();
        var file = Locate("data", "jsn", "product.json");
        Skip.If(file is null, "data/jsn/product.json not found.");

        var table = "IT_JsonExplode_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var flow = new FlowDefinition
        {
            Name = table,
            Source = new SourceSpec
            {
                Type = "json",
                Location = file!,
                Options = new Dictionary<string, string?> { ["explodePaths"] = "$.details" },
            },
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen, DefaultColumnType = "nvarchar(4000)" },
            Load = new LoadPolicy { Mode = LoadMode.TruncateLoad },
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.True(result.RowsLoaded > 0);
            Assert.Equal(result.RowsLoaded, await IntegrationDb.RowCountAsync(cs, table));

            // The exploded element fields become columns; the array itself is consumed (no 'details' column).
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "details_track_id"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "id"));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, table, "details"));

            // Explosion means more output rows than distinct products (at least one product has >1 track).
            var distinctProducts = await IntegrationDb.ScalarAsync<int>(cs,
                $"SELECT COUNT(DISTINCT [id]) FROM [dbo].[{table}]");
            Assert.True(result.RowsLoaded > distinctProducts,
                $"explode should yield more rows ({result.RowsLoaded}) than products ({distinctProducts})");
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private static string? Locate(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
