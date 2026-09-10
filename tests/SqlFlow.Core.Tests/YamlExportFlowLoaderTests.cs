using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The export YAML surface (flowType: exp): the smallest document maps with the documented defaults,
/// every exposed field reaches the model, the chunk policy's cross-field requirements fail at parse time, and
/// the writer's silent fallbacks (file type, encoding, compression) are rejected as errors instead.</summary>
public sealed class YamlExportFlowLoaderTests
{
    private static readonly YamlExportFlowLoader Loader = new();

    private const string Minimal = """
        flowType: exp
        name: orders-export
        connections:
          dwh: ${env:DWH}
        source:
          server: dwh
          object: DW.raw.Orders
        target:
          path: ./out
        """;

    [Fact]
    public void Minimal_MapsWithDefaults()
    {
        var doc = Loader.Parse(Minimal);
        var flow = doc.Flow;

        Assert.Equal("orders-export", flow.SysAlias);
        Assert.True(flow.FlowId > 0);
        Assert.Equal("dwh", flow.SrcServer);
        Assert.Equal("@dwh", flow.ConnectionReference);
        Assert.Equal("Orders", flow.Source.Name);
        Assert.Equal("raw", flow.Source.Schema);
        Assert.Equal("DW", flow.Source.Database);

        // The YAML default is one full file (no chunk policy needed), not the legacy table default of 'D'.
        Assert.Equal("F", flow.ExportBy);
        Assert.Equal(1, flow.ExportSize);
        Assert.Equal("./out", flow.TrgPath);
        Assert.Null(flow.TrgFileName);
        Assert.Equal("csv", flow.TrgFiletype);
        Assert.Null(flow.TrgEncoding);
        Assert.Equal("gzip", flow.CompressionType);
        Assert.Equal(";", flow.ColumnDelimiter);
        Assert.Equal("\"", flow.TextQualifier);
        Assert.True(flow.AddTimeStampToFileName);
        Assert.Null(flow.Subfolderpattern);
        Assert.Equal(0, flow.NoOfThreads);
        Assert.True(flow.OnErrorResume);
        Assert.Null(flow.SrcFilter);
        Assert.Equal("exp", flow.FlowType);

        var connection = Assert.Single(doc.Connections);
        Assert.Equal("dwh", connection.Alias);
        Assert.Equal(DataSourceKind.MSSQL, connection.Kind);
        Assert.Equal("${env:DWH}", connection.ConnectionRef);
        Assert.Equal(CredentialMode.InlineConnectionString, connection.Credential.Mode);
    }

    [Fact]
    public void FullDocument_MapsEveryField()
    {
        var doc = Loader.Parse("""
            flowType: exp
            name: orders-by-month
            batch: nightly
            connections:
              dwh: ${env:DWH}
            source:
              server: dwh
              object: DW.raw.Orders
              filter: Status = 'Open'
            export:
              by: month
              size: 2
              dateColumn: OrderDate
              fromDate: 2024-01-01
              toDate: 2024-12-31
              threads: 4
            target:
              path: ./out
              fileName: orders
              fileType: parquet
              compression: snappy
              subfolderPattern: YYYY/MM
              addTimestamp: false
            onErrorResume: false
            """);
        var flow = doc.Flow;

        Assert.Equal("nightly", flow.Batch);
        Assert.Equal("M", flow.ExportBy);
        Assert.Equal(2, flow.ExportSize);
        Assert.Equal("OrderDate", flow.DateColumn);
        Assert.Equal(new DateOnly(2024, 1, 1), flow.FromDate);
        Assert.Equal(new DateOnly(2024, 12, 31), flow.ToDate);
        Assert.Equal(4, flow.NoOfThreads);
        Assert.Equal("orders", flow.TrgFileName);
        Assert.Equal("parquet", flow.TrgFiletype);
        Assert.Equal("snappy", flow.CompressionType);
        Assert.Equal("YYYY/MM", flow.Subfolderpattern);
        Assert.False(flow.AddTimeStampToFileName);
        Assert.False(flow.OnErrorResume);

        // The filter normalizes to the planner's verbatim-append form, leading AND included.
        Assert.Equal(" AND Status = 'Open'", flow.SrcFilter);
    }

    [Fact]
    public void Filter_AuthorMayWriteTheAndThemselves()
    {
        var doc = Loader.Parse(Minimal.Replace("object: DW.raw.Orders",
            "object: DW.raw.Orders\n  filter: AND Status = 'Open'", StringComparison.Ordinal));

        Assert.Equal(" AND Status = 'Open'", doc.Flow.SrcFilter);
    }

    [Fact]
    public void KeyChunking_MapsColumn_AndCsvOptions()
    {
        var doc = Loader.Parse("""
            flowType: exp
            name: orders-by-key
            connections:
              dwh: ${env:DWH}
            source:
              server: dwh
              object: DW.raw.Orders
            export:
              by: key
              keyColumn: OrderID
              size: 50000
            target:
              path: ./out
              delimiter: "|"
              textQualifier: "'"
              encoding: utf16
            """);
        var flow = doc.Flow;

        Assert.Equal("K", flow.ExportBy);
        Assert.Equal("OrderID", flow.IncrementalColumn);
        Assert.Equal(50000, flow.ExportSize);
        Assert.Equal("|", flow.ColumnDelimiter);
        Assert.Equal("'", flow.TextQualifier);
        Assert.Equal("UTF-16", flow.TrgEncoding);
    }

    [Fact]
    public void InlineConnection_SynthesizesTheSourceName()
    {
        var doc = Loader.Parse("""
            flowType: exp
            name: inline-export
            source:
              connection: ${env:DWH}
              object: DW.raw.Orders
            target:
              path: ./out
            """);

        Assert.Equal("source", doc.Flow.SrcServer);
        var connection = Assert.Single(doc.Connections);
        Assert.Equal("source", connection.Alias);
    }

    [Theory]
    [InlineData("name: orders-export", "name: '  '", "'name' is required for an export flow.")]
    [InlineData("path: ./out", "path: ''", "'target.path' is required")]
    [InlineData("object: DW.raw.Orders", "object: raw.Orders", "'source.object'")]
    public void MissingRequirements_FailWithThePath(string find, string replace, string expectedError)
    {
        var yaml = Minimal.Replace(find, replace, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DayChunking_RequiresTheDateColumn()
    {
        var yaml = Minimal + "\nexport:\n  by: day\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("'export.dateColumn' is required when 'export.by' is day", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyChunking_RequiresTheKeyColumn()
    {
        var yaml = Minimal + "\nexport:\n  by: key\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("'export.keyColumn' is required when 'export.by' is key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DateWindow_RejectsFromAfterTo()
    {
        var yaml = Minimal + "\nexport:\n  by: day\n  dateColumn: OrderDate\n  fromDate: 2024-06-01\n  toDate: 2024-01-01\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("'export.fromDate' (2024-06-01) is after 'export.toDate' (2024-01-01)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export:\n  by: weekly", "'export.by' must be full, day, month, or key")]
    [InlineData("export:\n  size: 0", "'export.size' must be at least 1")]
    [InlineData("export:\n  threads: -1", "'export.threads' must be 0 (default) or more")]
    public void ChunkPolicy_RejectsBadValues(string extra, string expectedError)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal + "\n" + extra + "\n"));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fileType: xlsx", "'target.fileType' must be csv or parquet")]
    [InlineData("encoding: latin1", "'target.encoding' must be utf8, utf8bom, utf16, utf32, or ascii")]
    [InlineData("compression: zstd", "'target.compression' must be gzip, snappy, or none")]
    [InlineData("delimiter: ''", "'target.delimiter' must not be empty")]
    [InlineData("textQualifier: ''", "'target.textQualifier' must be a single character")]
    [InlineData("textQualifier: \"''\"", "'target.textQualifier' must be a single character")]
    public void Target_RejectsValuesTheWriterWouldSilentlyRemap(string extra, string expectedError)
    {
        var yaml = Minimal.Replace("path: ./out", "path: ./out\n  " + extra, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignProviderSource_IsRejectedAtParseTime()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: exp
            name: bad-export
            connections:
              shop:
                provider: mysql
                connection: ${env:SHOP}
            source:
              server: shop
              object: shop.shop.orders
            target:
              path: ./out
            """));

        Assert.Contains("the source connection 'shop' is 'MySQL'; an export flow's source must be SQL Server (mssql or azdb)",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentLoader_DispatchesExp_AndNamesTheNewKinds()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(), new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(), new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<ExportFlowDocument>(documents.Parse(Minimal));
        Assert.Equal("orders-export", doc.Document.Flow.SysAlias);

        var ex = Assert.Throws<FlowValidationException>(() => documents.Parse("flowType: bogus"));
        Assert.Contains("'exp' for a file export", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'sp' for a stored-procedure flow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyDeliveryFields_ReachTheModel()
    {
        var doc = Loader.Parse("""
            flowType: exp
            name: apc-matchedtrip-history
            connections:
              dwh: ${env:DWH}
            source:
              server: dwh
              object: DW.edw.APC_MatchedTrip
              withHint: WITH (INDEX([NCI_CalendarID]))
            target:
              path: https://acct.dfs.core.windows.net/export/APC_MatchedTrip/History
              encoding: utf8bom
              valueFormat: legacy
              zip: true
            export:
              by: month
              dateColumn: CalendarID
            """);

        var flow = doc.Flow;
        Assert.Equal("WITH (INDEX([NCI_CalendarID]))", flow.SrcWithHint);
        Assert.Equal("UTF8BOM", flow.TrgEncoding);
        Assert.Equal(ExportValueFormat.Legacy, flow.TrgValueFormat);
        Assert.True(flow.ZipTrg);
    }

    [Fact]
    public void LegacyDeliveryFields_DefaultToTheModernForm()
    {
        var flow = Loader.Parse(Minimal).Flow;

        Assert.Null(flow.SrcWithHint);
        Assert.Null(flow.TrgEncoding);
        Assert.Equal(ExportValueFormat.Iso, flow.TrgValueFormat);
        Assert.False(flow.ZipTrg);
    }

    [Fact]
    public void ValueFormat_RejectsAnUnknownToken()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: exp
            name: bad-value-format
            connections:
              dwh: ${env:DWH}
            source:
              server: dwh
              object: DW.raw.Orders
            target:
              path: ./out
              valueFormat: csvhelper
            """));

        Assert.Contains("valueFormat", ex.Message, StringComparison.Ordinal);
    }
}
