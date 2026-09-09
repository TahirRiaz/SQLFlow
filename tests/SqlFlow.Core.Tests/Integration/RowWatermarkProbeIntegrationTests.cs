using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The row-level watermark probe against the real catalog: it resolves the column's type to a
/// <see cref="WatermarkKind"/>, returns the typed <c>MAX</c> shifted back by the overlap, returns null for an
/// empty or missing target (so the caller does a full load), and fails clearly on a missing or non-orderable
/// column. These are the bounds the engine then pushes into every reader.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RowWatermarkProbeIntegrationTests
{
    private static string Q(string table) => $"[{IntegrationDb.Schema}].[{table}]";

    private static async Task<string> SeedAsync(string cs, string columns, string values)
    {
        var table = "IT_WmProbe_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE {Q(table)} ({columns});");
        if (!string.IsNullOrEmpty(values))
        {
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO {Q(table)} VALUES {values};");
        }

        return table;
    }

    [SkippableFact]
    public async Task IntegerColumn_ReturnsMaxMinusOverlap_AsIntegerKind()
    {
        var cs = IntegrationDb.Require();
        var table = await SeedAsync(cs, "[Id] int NOT NULL", "(1),(5),(3)");
        try
        {
            var result = await new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q(table), "Id", overlap: 2);
            Assert.NotNull(result);
            Assert.Equal(WatermarkKind.Whole, result!.Kind);
            Assert.Equal(3L, Convert.ToInt64(result.Value, CultureInfo.InvariantCulture)); // MAX 5 - 2
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DateTimeColumn_ReturnsMaxMinusOverlapDays_AsDateTimeKind()
    {
        var cs = IntegrationDb.Require();
        var table = await SeedAsync(cs, "[Ts] datetime2(3) NOT NULL", "('2026-01-10'),('2026-01-20')");
        try
        {
            var result = await new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q(table), "Ts", overlap: 7);
            Assert.NotNull(result);
            Assert.Equal(WatermarkKind.DateTime, result!.Kind);
            Assert.Equal(new DateTime(2026, 1, 13), Convert.ToDateTime(result.Value, CultureInfo.InvariantCulture)); // 01-20 minus 7 days
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DecimalColumn_ReturnsDecimalKind()
    {
        var cs = IntegrationDb.Require();
        var table = await SeedAsync(cs, "[Amount] decimal(12,2) NOT NULL", "(10.50),(99.99)");
        try
        {
            var result = await new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q(table), "Amount", overlap: 0);
            Assert.NotNull(result);
            Assert.Equal(WatermarkKind.Fixed, result!.Kind);
            Assert.Equal(99.99m, Convert.ToDecimal(result.Value, CultureInfo.InvariantCulture));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task EmptyTable_ReturnsNull_ForFullLoad()
    {
        var cs = IntegrationDb.Require();
        var table = await SeedAsync(cs, "[Id] int NOT NULL", values: "");
        try
        {
            Assert.Null(await new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q(table), "Id", overlap: 0));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task MissingTable_ReturnsNull_ForFullLoad()
    {
        var cs = IntegrationDb.Require();
        Assert.Null(await new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q("IT_WmProbe_does_not_exist"), "Id", overlap: 0));
    }

    [SkippableFact]
    public async Task MissingColumn_Throws()
    {
        var cs = IntegrationDb.Require();
        var table = await SeedAsync(cs, "[Id] int NOT NULL", "(1)");
        try
        {
            await Assert.ThrowsAsync<SqlFlowException>(() => new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q(table), "Nope", overlap: 0));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task NonOrderableColumn_Throws()
    {
        var cs = IntegrationDb.Require();
        var table = await SeedAsync(cs, "[Key] uniqueidentifier NOT NULL", "(NEWID())");
        try
        {
            await Assert.ThrowsAsync<SqlFlowException>(() => new SqlServerIncrementalProbe().GetRowWatermarkAsync(cs, Q(table), "Key", overlap: 0));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }
}
