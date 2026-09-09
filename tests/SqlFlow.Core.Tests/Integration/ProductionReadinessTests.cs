using System.Globalization;
using System.Text;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Production-readiness checks for the CSV-to-SQL-Server path, run against the physical sink: exact
/// data fidelity on adversarial content, volume/streaming at scale, real concurrency (real bulk loader
/// and connections, not fakes), error attribution, idempotency, and stability across many runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductionReadinessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_prod_" + Guid.NewGuid().ToString("N"));

    public ProductionReadinessTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Csv(string name, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return new SourceSpec { Type = "csv", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private static FlowDefinition Flow(string cs, SourceSpec source, string table, string? defaultType = null, LoadMode mode = LoadMode.Append)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen, DefaultColumnType = defaultType ?? "varchar(255)" },
            Load = new LoadPolicy { Mode = mode },
        };

    [SkippableFact]
    public async Task Fidelity_RoundTripsAdversarialContent()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Fidelity_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        // Unicode, embedded comma/quote/newline in quoted fields, leading zeros, empty, surrounding spaces.
        var content =
            "Id,Value\n" +
            "1,Ærø\n" +
            "2,Москва\n" +
            "3,\"Acme, Inc.\"\n" +
            "4,\"say \"\"hi\"\"\"\n" +
            "5,\"line1\nline2\"\n" +
            "6,007\n" +
            "7,\n" +
            "8,  spaced  \n";

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("adversarial.csv", content), table, defaultType: "nvarchar(4000)"));
            Assert.Equal(FlowStatus.Success, result.Status);

            async Task<string?> Value(string id) =>
                await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Value] FROM [dbo].[{table}] WHERE [Id] = '{id}'");

            Assert.Equal("Ærø", await Value("1"));
            Assert.Equal("Москва", await Value("2"));
            Assert.Equal("Acme, Inc.", await Value("3"));
            Assert.Equal("say \"hi\"", await Value("4"));
            Assert.Equal("line1\nline2", await Value("5"));
            Assert.Equal("007", await Value("6"));
            Assert.Null(await Value("7"));
            Assert.Equal("  spaced  ", await Value("8"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Scale_StreamsFiftyThousandRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Scale_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        const int rows = 50_000;
        var sb = new StringBuilder("Id,Name,Value\n");
        for (var i = 1; i <= rows; i++)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(",Item").Append(i).Append(',').Append(i * 2).Append('\n');
        }

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("big.csv", sb.ToString()), table));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(rows, result.RowsLoaded);
            Assert.Equal(rows, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Concurrency_ManyPipelinesIntoDistinctTables_StayIsolated()
    {
        var cs = IntegrationDb.Require();
        const int pipelines = 24;
        var tables = Enumerable.Range(1, pipelines)
            .Select(i => (Index: i, Table: $"IT_Conc_{Guid.NewGuid():N}".Substring(0, 18) + "_" + i))
            .ToList();

        try
        {
            foreach (var (_, table) in tables)
            {
                await IntegrationDb.DropTableAsync(cs, table);
            }

            // Each pipeline loads a distinct row count into its own table, all at once, through the real
            // bulk loader and real connections.
            var results = await Task.WhenAll(tables.Select(t =>
            {
                var content = "OrderId\n" + string.Concat(Enumerable.Range(1, t.Index).Select(n => n + "\n"));
                return IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv($"c{t.Index}.csv", content), t.Table));
            }));

            Assert.All(results, r => Assert.Equal(FlowStatus.Success, r.Status));
            foreach (var (index, table) in tables)
            {
                Assert.Equal(index, await IntegrationDb.RowCountAsync(cs, table));
            }
        }
        finally
        {
            foreach (var (_, table) in tables)
            {
                await IntegrationDb.DropTableAsync(cs, table);
            }
        }
    }

    [SkippableFact]
    public async Task Faulty_MidFileBadRow_FailsWithFileAndRowAttribution()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Bad_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var source = Csv("bad.csv", "A,B,C\n1,2,3\n4,5\n", new() { ["expectedColumnCount"] = "3" });

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, source, table));

            Assert.Equal(FlowStatus.Failed, result.Status);
            Assert.NotNull(result.Error);
            Assert.Contains("bad.csv", result.Error!, StringComparison.Ordinal);
            Assert.Contains("row", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task TruncateLoad_IsIdempotentAcrossRuns()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Trunc_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("first.csv", "OrderId\n1\n2\n3\n4\n5\n", null), table, mode: LoadMode.TruncateLoad));
            await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("second.csv", "OrderId\n9\n", null), table, mode: LoadMode.TruncateLoad));

            // After a truncate-load the table reflects only the latest file, no matter the prior state.
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Repeat_ManySequentialRuns_AreStable()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Repeat_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        const int runs = 30;

        try
        {
            for (var i = 0; i < runs; i++)
            {
                var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, Csv("rep.csv", "OrderId\n1\n2\n", null), table));
                Assert.Equal(FlowStatus.Success, result.Status);
            }

            // No leaked connections or accumulated state: every append landed.
            Assert.Equal(runs * 2, await IntegrationDb.RowCountAsync(cs, table));
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
