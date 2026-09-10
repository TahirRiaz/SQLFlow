using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Reads a real legacy binary <c>.xls</c> workbook (data/xls/sales-data-2014.xls) end to end through
/// the XLS reader. This is the genuine <c>.xls</c> coverage that ClosedXML (xlsx-only) cannot provide:
/// it asserts the exact sheet, columns, row count, and concrete cell values including date and numeric
/// rendering. Skips if the file is absent so the suite stays runnable everywhere.
/// </summary>
public sealed class SalesDataXlsTests
{
    [SkippableFact]
    public async Task SalesData2014_ParsesExactly()
    {
        var path = LocateFixture("data", "xls", "sales-data-2014.xls");
        Skip.If(path is null, "data/xls/sales-data-2014.xls not found.");

        var reader = new XlsSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);
        var source = new SourceSpec { Type = "xls", Location = path!, Options = new Dictionary<string, string?>() };

        var columns = (await reader.GetColumnsAsync(source)).ToList();
        var names = columns.Select(c => c.Name).ToList();

        // The 21 source columns from the 'Orders' sheet, in order, AFTER the legacy file-flow cleanup:
        // every space and the hyphen in "Sub-Category" become underscores (the exact names a legacy install
        // produced for this file), so an existing legacy table lines up column-for-column.
        string[] expected =
        [
            "Row_ID", "Order_ID", "Order_Date", "Ship_Date", "Ship_Mode", "Customer_ID", "Customer_Name",
            "Segment", "Country", "City", "State", "Postal_Code", "Region", "Product_ID", "Category",
            "Sub_Category", "Product_Name", "Sales", "Quantity", "Discount", "Profit",
        ];
        Assert.Equal(expected, names.Take(expected.Length).ToArray());
        Assert.Contains("FileName_DW", names); // provenance still injected

        int Col(string name) => names.IndexOf(name);

        var read = await reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = new List<object?[]>();
        while (await data.ReadAsync())
        {
            var row = new object?[data.FieldCount];
            for (var i = 0; i < data.FieldCount; i++)
            {
                row[i] = data.IsDBNull(i) ? null : data.GetValue(i);
            }

            rows.Add(row);
        }

        Assert.Equal(1993, rows.Count);

        // First data row, with date and numeric cells rendered as raw strings.
        var first = rows[0];
        Assert.Equal("1546", first[Col("Row_ID")]);                    // numeric -> "1546"
        Assert.Equal("CA-2014-150245", first[Col("Order_ID")]);
        Assert.Equal("Pamela Coakley", first[Col("Customer_Name")]);
        Assert.Equal("United States", first[Col("Country")]);
        Assert.Equal("2014-12-31 00:00:00", first[Col("Order_Date")]); // date -> invariant string
        Assert.Equal("1573.488", first[Col("Sales")]);                 // decimal preserved
        Assert.Equal("7", first[Col("Quantity")]);
    }

    private static string? LocateFixture(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
