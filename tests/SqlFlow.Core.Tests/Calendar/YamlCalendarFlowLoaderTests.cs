using SqlFlow.Core;
using SqlFlow.Core.Calendar;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Core.Tests.Calendar;

public sealed class YamlCalendarFlowLoaderTests
{
    private const string Minimal = """
        flowType: cal
        name: dwh_dim_calendar_00_cal
        batch: calendar
        connections:
          target: "Server=localhost;Database=dw;Integrated Security=true;"
        calendar:
          server: target
          object: dw-dwh-prod.edw.Dim_Calendar
          from: 2013-01-01
          to: 2035-12-31
          country: NO
        """;

    private static readonly YamlCalendarFlowLoader Loader = new();

    [Fact]
    public void ParsesTheMinimalDocumentAndDefaultsFromTheCountry()
    {
        var flow = Loader.Parse(Minimal).Flow;

        Assert.Equal("dwh_dim_calendar_00_cal", flow.SysAlias);
        Assert.Equal("calendar", flow.Batch);
        Assert.Equal("target", flow.Server);
        Assert.Equal("dw-dwh-prod", flow.Table.Database);
        Assert.Equal("edw", flow.Table.Schema);
        Assert.Equal("Dim_Calendar", flow.Table.Name);
        Assert.Equal(new DateOnly(2013, 1, 1), flow.From);
        Assert.Equal(new DateOnly(2035, 12, 31), flow.To);
        Assert.Equal("NO", flow.Country);

        // Culture and zone default from the country rather than from the host.
        Assert.Equal("nb-NO", flow.Culture);
        Assert.Equal("Europe/Oslo", flow.TimeZone);
        Assert.Equal(1, flow.FiscalYearStartMonth);
        Assert.Equal(CalendarObservanceSet.Full, flow.Observances);
        Assert.False(flow.Rebuild);
        Assert.Equal("cal", flow.FlowType);
    }

    [Fact]
    public void HonoursExplicitOverrides()
    {
        var flow = Loader.Parse(Minimal.Replace(
            "  country: NO",
            """
              country: NO
              culture: en-GB
              timezone: UTC
              fiscalYearStartMonth: 4
              observances: publicHolidays
              rebuild: true
            """,
            StringComparison.Ordinal)).Flow;

        Assert.Equal("en-GB", flow.Culture);
        Assert.Equal("UTC", flow.TimeZone);
        Assert.Equal(4, flow.FiscalYearStartMonth);
        Assert.Equal(CalendarObservanceSet.PublicHolidays, flow.Observances);
        Assert.True(flow.Rebuild);
    }

    [Fact]
    public void RequiresTheCalendarBlock()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: cal
            name: c
            """));
        Assert.Contains("'calendar' is required", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  from: 2013-01-01", "  from: 01/01/2013", "not a date in yyyy-MM-dd form")]
    [InlineData("  to: 2035-12-31", "  to: 2012-12-31", "is before")]
    [InlineData("  country: NO", "  country: SE", "is not supported")]
    [InlineData("  country: NO", "  country: NO\n  culture: \"@@@\"", "is not a culture")]
    [InlineData("  country: NO", "  country: NO\n  timezone: Mars/Olympus", "is not a time zone")]
    [InlineData("  country: NO", "  country: NO\n  fiscalYearStartMonth: 13", "must be 1 through 12")]
    [InlineData("  country: NO", "  country: NO\n  observances: sometimes", "'calendar.observances' is 'sometimes'")]
    [InlineData("  to: 2035-12-31", "  to: 2400-12-31", "more than the")]
    public void RejectsInvalidConfiguration(string find, string replace, string expected)
    {
        var yaml = Minimal.Replace(find, replace, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresTheObjectAndTheDates()
    {
        foreach (var (line, expected) in new[]
        {
            ("  object: dw-dwh-prod.edw.Dim_Calendar", "'calendar.object' is required"),
            ("  from: 2013-01-01", "'calendar.from' is required"),
            ("  to: 2035-12-31", "'calendar.to' is required"),
            ("  country: NO", "'calendar.country' is required"),
        })
        {
            var yaml = string.Join(
                "\n",
                Minimal.Split('\n').Where(l => !l.TrimEnd('\r').Equals(line, StringComparison.Ordinal)));
            var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DispatchesThroughTheDocumentLoader()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
            new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<CalendarFlowDocument>(documents.Parse(Minimal));
        Assert.Equal("dwh_dim_calendar_00_cal", doc.Document.Flow.SysAlias);
    }

    [Fact]
    public void CarriesTheDocumentSchedule()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
            new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = documents.Parse(Minimal + """

            schedule:
              name: calendar_yearly
              cron: "0 3 2 1 *"
              timezone: Europe/Oslo
            """);

        Assert.NotNull(doc.Schedule);
        Assert.Equal("calendar_yearly", doc.Schedule!.Name);
        Assert.Equal("0 3 2 1 *", doc.Schedule.Cron);
    }
}
