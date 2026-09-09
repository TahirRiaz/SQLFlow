using ClosedXML.Excel;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end Excel ingestion against the physical sink, proving the XLS reader rides the same engine
/// path as CSV: a generated .xlsx is read via ExcelDataReader and bulk-loaded into a real table with
/// the provenance columns, all through the shared pipeline.
/// </summary>
[Trait("Category", "Integration")]
public sealed class XlsIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_xlsit_" + Guid.NewGuid().ToString("N"));

    public XlsIntegrationTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task XlsLoad_CreatesTableAndInsertsRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Xls_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var path = Path.Combine(_dir, "orders.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Orders");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(1, 2).Value = "Customer";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = "Acme";
            ws.Cell(3, 1).Value = 2;
            ws.Cell(3, 2).Value = "Globex";
            ws.Cell(4, 1).Value = 3;
            ws.Cell(4, 2).Value = "Initech";
            wb.SaveAs(path);
        }

        var flow = new FlowDefinition
        {
            Name = table,
            Source = new SourceSpec { Type = "xlsx", Location = path },
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "OrderId"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "FileName_DW"));
            Assert.Equal("Globex", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [Customer] FROM [dbo].[{table}] WHERE [OrderId] = '2'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task RealXls_SalesData_LoadsAllRowsIntoSink()
    {
        var cs = IntegrationDb.Require();
        var file = LocateFixture("data", "xls", "sales-data-2014.xls");
        Skip.If(file is null, "data/xls/sales-data-2014.xls not found.");

        var table = "IT_Sales_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var flow = new FlowDefinition
        {
            Name = table,
            Source = new SourceSpec { Type = "xls", Location = file!, Options = new Dictionary<string, string?>() },
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
        };

        try
        {
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(1993, result.RowsLoaded);
            Assert.Equal(1993, await IntegrationDb.RowCountAsync(cs, table));
            // The legacy file cleanup names the target columns (spaces -> underscores), so the table matches
            // what a legacy install built for this spreadsheet.
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "Order_ID"));
            Assert.Equal("Pamela Coakley", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [Customer_Name] FROM [dbo].[{table}] WHERE [Row_ID] = '1546'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private static string? LocateFixture(params string[] parts)
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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
