using System.Data;
using System.Data.Common;
using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Incremental;

/// <summary>
/// The reader-agnostic row-level filter that the engine wraps around every source reader: it drops rows at or
/// below the watermark before they reach the bulk loader, keeps rows past it, excludes null watermark cells, and
/// fails loudly when the watermark column is absent. Backed by a real <see cref="DataTableReader"/> so the full
/// <see cref="DbDataReader"/> contract (sync and async reads, ordinals, values) is exercised, not a stub.
/// </summary>
public sealed class WatermarkFilteringDataReaderTests
{
    private static DbDataReader IntReader(string column, params object?[] ids)
    {
        var table = new DataTable();
        var col = table.Columns.Add(column, typeof(int));
        col.AllowDBNull = true;
        foreach (var id in ids)
        {
            table.Rows.Add(id ?? (object)DBNull.Value);
        }

        return table.CreateDataReader();
    }

    private static async Task<List<int?>> DrainAsync(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        var result = new List<int?>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal));
        }

        return result;
    }

    [Fact]
    public async Task KeepsOnlyRowsStrictlyPastTheBound()
    {
        var inner = IntReader("Id", 1, 2, 3, 4, 5);
        await using var filtered = new WatermarkFilteringDataReader(inner, "Id", "2", WatermarkKind.Whole);

        Assert.Equal([3, 4, 5], await DrainAsync(filtered, "Id"));
    }

    [Fact]
    public void Read_Sync_FiltersToo()
    {
        var inner = IntReader("Id", 1, 2, 3);
        using var filtered = new WatermarkFilteringDataReader(inner, "Id", "2", WatermarkKind.Whole);
        var ordinal = filtered.GetOrdinal("Id");

        var kept = new List<int>();
        while (filtered.Read())
        {
            kept.Add(filtered.GetInt32(ordinal));
        }

        Assert.Equal([3], kept);
    }

    [Fact]
    public async Task NullWatermarkCells_AreExcluded()
    {
        var inner = IntReader("Id", 1, null, 5);
        await using var filtered = new WatermarkFilteringDataReader(inner, "Id", "0", WatermarkKind.Whole);

        Assert.Equal([1, 5], await DrainAsync(filtered, "Id"));
    }

    [Fact]
    public async Task EmptyResult_WhenEverythingIsAtOrBelowTheBound()
    {
        var inner = IntReader("Id", 1, 2, 3);
        await using var filtered = new WatermarkFilteringDataReader(inner, "Id", "9", WatermarkKind.Whole);

        Assert.Empty(await DrainAsync(filtered, "Id"));
    }

    [Fact]
    public async Task MissingWatermarkColumn_ThrowsWithAvailableColumns()
    {
        var inner = IntReader("Other", 1, 2);
        await using var filtered = new WatermarkFilteringDataReader(inner, "Id", "0", WatermarkKind.Whole);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            while (await filtered.ReadAsync())
            {
            }
        });

        Assert.Contains("Id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Other", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsCaseInsensitiveOnColumnName()
    {
        var inner = IntReader("Id", 1, 2, 3);
        await using var filtered = new WatermarkFilteringDataReader(inner, "id", "1", WatermarkKind.Whole);

        Assert.Equal([2, 3], await DrainAsync(filtered, "Id"));
    }

    [Fact]
    public async Task PassesThroughOtherColumnsUnchanged()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add(1, "a");
        table.Rows.Add(2, "b");
        table.Rows.Add(3, "c");

        await using var filtered = new WatermarkFilteringDataReader(table.CreateDataReader(), "Id", "1", WatermarkKind.Whole);

        Assert.Equal(2, filtered.FieldCount);
        var names = new List<string>();
        while (await filtered.ReadAsync())
        {
            names.Add(filtered.GetString(filtered.GetOrdinal("Name")));
        }

        Assert.Equal(["b", "c"], names);
    }
}
