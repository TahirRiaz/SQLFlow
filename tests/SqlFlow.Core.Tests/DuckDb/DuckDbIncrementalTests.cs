using System.Globalization;
using DuckDB.NET.Data;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>
/// Row-level incremental for the DuckDB reader: the engine-injected watermark options become a pushdown WHERE
/// predicate (combined with any user filter), so DuckDB reads only rows past the bound and skips row groups below
/// it. Covers the pure predicate building and a native end-to-end read. Skips the native part if libduckdb cannot
/// load.
/// </summary>
public sealed class DuckDbIncrementalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_duckinc_" + Guid.NewGuid().ToString("N"));

    public DuckDbIncrementalTests() => Directory.CreateDirectory(_dir);

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

    private static SourceSpec Source(string? location, params (string Key, string? Value)[] options)
        => new() { Type = "duckdb", Location = location, Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase) };

    private static SourceSpec WithWatermark(string? location, string column, string value, WatermarkKind kind, params (string Key, string? Value)[] extra)
    {
        var options = new List<(string, string?)>
        {
            (WatermarkPredicate.ColumnOption, column),
            (WatermarkPredicate.ValueOption, value),
            (WatermarkPredicate.KindOption, kind.ToString()),
        };
        options.AddRange(extra);
        return Source(location, options.ToArray());
    }

    [Fact]
    public void IncrementalPredicate_NullWhenNoWatermark()
        => Assert.Null(DuckDbQuery.IncrementalPredicate(Source("/d/x.parquet")));

    [Fact]
    public void IncrementalPredicate_BuildsPushdownExpression()
        => Assert.Equal("\"Id\" > 5", DuckDbQuery.IncrementalPredicate(WithWatermark("/d/x.parquet", "Id", "5", WatermarkKind.Whole)));

    [Fact]
    public void CombineFilters_AndsUserFilterWithWatermark()
        => Assert.Equal("(id = 2) AND (\"Id\" > 5)",
            DuckDbQuery.CombineFilters("id = 2", DuckDbQuery.IncrementalPredicate(WithWatermark("/d/x.parquet", "Id", "5", WatermarkKind.Whole))));

    [Fact]
    public void CombineFilters_EitherSideMayBeBlank()
    {
        Assert.Equal("a = 1", DuckDbQuery.CombineFilters("a = 1", null));
        Assert.Equal("b = 2", DuckDbQuery.CombineFilters(null, "b = 2"));
        Assert.Null(DuckDbQuery.CombineFilters(null, "   "));
    }

    [SkippableFact]
    public async Task NativeRead_PushesDownTheWatermark()
    {
        Skip.IfNot(Available.Value, "libduckdb (DuckDB.NET native) could not be loaded.");

        var path = Path.Combine(_dir, "data.parquet").Replace('\\', '/');
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"COPY (SELECT * FROM (VALUES (1),(2),(3),(4),(5),(6)) t(id)) TO '{path}' (FORMAT PARQUET);";
            command.ExecuteNonQuery();
        }

        var reader = new DuckDbSourceReader();
        var source = WithWatermark(path, "id", "3", WatermarkKind.Whole);

        var columns = await reader.GetColumnsAsync(source);
        var read = await reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var ids = new List<long>();
        while (await data.ReadAsync())
        {
            ids.Add(Convert.ToInt64(data.GetValue(0), CultureInfo.InvariantCulture));
        }

        Assert.Equal([4L, 5L, 6L], ids.OrderBy(x => x).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
