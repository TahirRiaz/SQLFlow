using System.Globalization;
using DuckDB.NET.Data;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>
/// The DuckDB reader against the real native engine: it writes a small Parquet fixture with DuckDB, then reads
/// it back through <see cref="DuckDbSourceReader"/>, asserting typed columns, streamed values, predicate/column
/// pushdown, and JSON projection of a nested column. Skips if libduckdb cannot load (so the suite still runs
/// where the native library is unavailable).
/// </summary>
public sealed class DuckDbSourceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_duckdb_" + Guid.NewGuid().ToString("N"));

    public DuckDbSourceReaderTests() => Directory.CreateDirectory(_dir);

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    private string WriteParquet(string copySelect)
    {
        Skip.IfNot(Available.Value, "libduckdb (DuckDB.NET native) could not be loaded.");
        var path = Path.Combine(_dir, "data.parquet").Replace('\\', '/');
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY ({copySelect}) TO '{path}' (FORMAT PARQUET);";
        command.ExecuteNonQuery();
        return path;
    }

    private static SourceSpec Source(string path, params (string Key, string? Value)[] options)
        => new() { Type = "duckdb", Location = path, Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase) };

    private static async Task<List<object?[]>> ReadAllAsync(DuckDbSourceReader reader, SourceSpec source)
    {
        var columns = await reader.GetColumnsAsync(source);
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

        return rows;
    }

    [SkippableFact]
    public async Task ReadsTypedColumnsAndRows()
    {
        var path = WriteParquet("SELECT * FROM (VALUES (1,'Ann',CAST(10.50 AS DECIMAL(8,2))),(2,'Bob',CAST(20.00 AS DECIMAL(8,2)))) t(id,name,amt)");
        var reader = new DuckDbSourceReader();
        var source = Source(path);

        var columns = await reader.GetColumnsAsync(source);
        Assert.Equal(["id", "name", "amt"], columns.Select(c => c.Name).ToArray());
        Assert.Equal("int", columns[0].SqlType);
        Assert.Equal("nvarchar(max)", columns[1].SqlType);
        Assert.Equal("decimal(8,2)", columns[2].SqlType);

        var rows = await ReadAllAsync(reader, source);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Ann", rows[0][1]);
        Assert.Equal(2, Convert.ToInt32(rows[1][0], CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task PushesDownColumnsAndFilter()
    {
        var path = WriteParquet("SELECT * FROM (VALUES (1,'Ann'),(2,'Bob'),(3,'Cy')) t(id,name)");
        var reader = new DuckDbSourceReader();
        var source = Source(path, ("columns", "name"), ("filter", "id = 2"));

        var columns = await reader.GetColumnsAsync(source);
        Assert.Equal(["name"], columns.Select(c => c.Name).ToArray()); // projection

        var rows = await ReadAllAsync(reader, source);
        var row = Assert.Single(rows);                                  // predicate pushdown
        Assert.Equal("Bob", row[0]);
    }

    [SkippableFact]
    public async Task ProjectsNestedColumnsAsJson()
    {
        var path = WriteParquet("SELECT [10,20,30] AS nums, 1 AS id");
        var reader = new DuckDbSourceReader();
        var source = Source(path);

        var columns = await reader.GetColumnsAsync(source);
        var nums = columns.Single(c => c.Name == "nums");
        Assert.Equal("nvarchar(max)", nums.SqlType); // a LIST becomes JSON text

        var rows = await ReadAllAsync(reader, source);
        Assert.Equal("[10,20,30]", Assert.IsType<string>(rows[0][columns.ToList().FindIndex(c => c.Name == "nums")]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
