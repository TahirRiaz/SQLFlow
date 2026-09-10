using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Export.Legacy;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ExportFlowMappingTests
{
    private static LegacyExportRow Row() => new()
    {
        FlowID = 4,
        SysAlias = "sys",
        srcServer = "src",
        srcDBSchTbl = "[DB].[dbo].[Orders]",
    };

    [Fact]
    public void Maps_RequiredFields_AndDefaults()
    {
        var flow = ExportFlowMapper.FromLegacy(Row());

        Assert.Equal(4, flow.FlowId);
        Assert.Equal("src", flow.SrcServer);
        Assert.Equal("@src", flow.ConnectionReference);
        Assert.Equal("Orders", flow.Source.Name);
        Assert.Equal("D", flow.ExportBy);
        Assert.Equal(1, flow.ExportSize);
        Assert.Equal("csv", flow.TrgFiletype);
        Assert.Equal(";", flow.ColumnDelimiter);
        Assert.Equal("\"", flow.TextQualifier);
        Assert.True(flow.AddTimeStampToFileName);
        Assert.Null(flow.TargetReference);
    }

    [Fact]
    public void ServicePrincipalAlias_CollapsesToTargetReference()
    {
        var row = Row();
        row.ServicePrincipalAlias = "adls1";
        Assert.Equal("@adls1", ExportFlowMapper.FromLegacy(row).TargetReference);
    }

    [Fact]
    public void MissingSource_Throws()
    {
        var row = Row();
        row.srcDBSchTbl = null;
        Assert.Throws<SqlFlowException>(() => ExportFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void HonorsConfiguredCsvOptions()
    {
        var row = Row();
        row.ColumnDelimiter = "|";
        row.TextQualifier = "'";
        row.trgEncoding = "Unicode";
        row.trgFiletype = "parquet";
        var flow = ExportFlowMapper.FromLegacy(row);
        Assert.Equal("|", flow.ColumnDelimiter);
        Assert.Equal("'", flow.TextQualifier);
        Assert.Equal("Unicode", flow.TrgEncoding);
        Assert.Equal("parquet", flow.TrgFiletype);
    }

    [Fact]
    public void LegacyRow_KeepsTheBytesTheLegacyEngineWrote()
    {
        var flow = ExportFlowMapper.FromLegacy(Row());

        // A flow defined in the legacy control database already has a consumer parsing its files, so it renders
        // values the way legacy CsvHelper did and keeps the UTF-8 byte-order mark legacy's cloud writer emitted.
        Assert.Equal(ExportValueFormat.Legacy, flow.TrgValueFormat);
        Assert.Equal("UTF8BOM", flow.TrgEncoding);
    }

    [Fact]
    public void LegacyRow_HonorsAnExplicitEncoding()
    {
        var row = Row();
        row.trgEncoding = "UTF-16";

        Assert.Equal("UTF-16", ExportFlowMapper.FromLegacy(row).TrgEncoding);
    }

    [Fact]
    public void BracketedLegacyColumns_AreUnbracketed()
    {
        var row = Row();
        row.ExportBy = "K";
        row.IncrementalColumn = "[MatchedTripPK]";
        row.DateColumn = "[CalendarID]";

        var flow = ExportFlowMapper.FromLegacy(row);

        // The planner brackets whatever it is handed, so leaving these bracketed produced [[CalendarID]]].
        Assert.Equal("MatchedTripPK", flow.IncrementalColumn);
        Assert.Equal("CalendarID", flow.DateColumn);
    }

    [Theory]
    [InlineData("M")]
    [InlineData("D")]
    public void MonthOrDayExport_ChunksOnTheIncrementalColumn_WhenDateColumnIsEmpty(string exportBy)
    {
        var row = Row();
        row.ExportBy = exportBy;
        row.IncrementalColumn = "[CalendarID]";
        row.DateColumn = null;

        // Legacy fell through to a date-typed IncrementalColumn here; without the fallback the planner rejects
        // the flow for having no DateColumn, which is every legacy delivery export windowed on its own date.
        Assert.Equal("CalendarID", ExportFlowMapper.FromLegacy(row).DateColumn);
    }

    [Fact]
    public void KeyExport_DoesNotBorrowTheIncrementalColumnAsADate()
    {
        var row = Row();
        row.ExportBy = "K";
        row.IncrementalColumn = "[MatchedTripPK]";
        row.DateColumn = null;

        Assert.Null(ExportFlowMapper.FromLegacy(row).DateColumn);
    }
}
