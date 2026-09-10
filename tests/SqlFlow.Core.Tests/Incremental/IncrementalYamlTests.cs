using SqlFlow.Core;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Incremental;

/// <summary>
/// The YAML surface for incremental loading: the file-date knobs and the row-level watermark knobs map onto the
/// model, and the two modes are kept mutually exclusive so a misconfiguration fails loud at load time rather than
/// silently ignoring a knob at run time.
/// </summary>
public sealed class IncrementalYamlTests
{
    private static string Doc(string incremental) => """
        name: t
        source:
          type: parquet
          location: /data/x.parquet
        target:
          connection: ${env:CS}
          schema: dbo
          table: T
        incremental:
        __INCREMENTAL__
        """.Replace("__INCREMENTAL__", incremental, StringComparison.Ordinal);

    private static SqlFlow.Core.Model.FlowDefinition Load(string incremental)
        => new YamlFlowLoader().Parse(Doc(incremental));

    [Fact]
    public void RowLevel_WatermarkColumn_AndOverlap_Map()
    {
        var flow = Load("  watermarkColumn: LoadId\n  watermarkOverlap: 5");
        Assert.Equal("LoadId", flow.Incremental!.WatermarkColumn);
        Assert.Equal(5, flow.Incremental.WatermarkOverlap);
    }

    [Fact]
    public void FileDate_DateColumn_AndOverlapDays_Map()
    {
        var flow = Load("  dateColumn: OrderDate\n  overlapDays: 3");
        Assert.Equal("OrderDate", flow.Incremental!.DateColumn);
        Assert.Equal(3, flow.Incremental.OverlapDays);
        Assert.Null(flow.Incremental.WatermarkColumn);
    }

    [Fact]
    public void WatermarkColumn_DefaultsToZeroOverlap()
    {
        var flow = Load("  watermarkColumn: Id");
        Assert.Equal("Id", flow.Incremental!.WatermarkColumn);
        Assert.Equal(0, flow.Incremental.WatermarkOverlap);
    }

    [Fact]
    public void WatermarkColumn_WithOverlapDays_IsRejected()
        => Assert.Throws<SqlFlowException>(() => Load("  watermarkColumn: Id\n  overlapDays: 3"));

    [Fact]
    public void WatermarkColumn_WithDateColumn_IsRejected()
        => Assert.Throws<SqlFlowException>(() => Load("  watermarkColumn: Id\n  dateColumn: OrderDate"));

    [Fact]
    public void WatermarkOverlap_WithoutWatermarkColumn_IsRejected()
        => Assert.Throws<SqlFlowException>(() => Load("  watermarkOverlap: 5"));

    [Fact]
    public void NegativeWatermarkOverlap_IsRejected()
        => Assert.Throws<SqlFlowException>(() => Load("  watermarkColumn: Id\n  watermarkOverlap: -1"));
}
