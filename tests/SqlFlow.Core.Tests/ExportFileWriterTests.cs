using System.Data;
using System.Text;
using Parquet;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Export;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ExportFileWriterTests
{
    private static DataTable Sample()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Rows.Add(1, "Ann", 10.5m);
        table.Rows.Add(2, "B;ig\"quote", 20m);
        table.Rows.Add(3, DBNull.Value, DBNull.Value);
        return table;
    }

    [Fact]
    public async Task Csv_WritesHeader_Delimiter_Quoting_AndNulls()
    {
        using var table = Sample();
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(new CsvWriteOptions
        {
            Delimiter = ";",
            Qualifier = '"',
            Encoding = new UTF8Encoding(false),
        });

        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);

        Assert.Equal(3, rows);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Id;Name;Amount", lines[0]);
        Assert.Equal("1;\"Ann\";10.5", lines[1]);                       // string column quoted, numbers bare
        Assert.Equal("2;\"B;ig\"\"quote\";20", lines[2]);               // embedded delimiter + doubled quote
        Assert.Equal("3;;", lines[3]);                                  // nulls -> empty fields
    }

    // The shape of a real arc/edw export row: the column types the legacy APC_MatchedTrip delivery carries,
    // in order, so the rendering assertions below are the ones a live consumer actually parses.
    private static DataTable LegacyShape()
    {
        var table = new DataTable();
        table.Columns.Add("MatchedTripPK", typeof(int));
        table.Columns.Add("CalendarID", typeof(DateTime));       // SQL date, read back as midnight DateTime
        table.Columns.Add("SourceSystemID", typeof(byte));       // tinyint
        table.Columns.Add("TripFactsPK", typeof(int));           // nullable, NULL in this row
        table.Columns.Add("TripId", typeof(string));
        table.Columns.Add("Latitude", typeof(decimal));
        table.Columns.Add("ArrivalPlan_DW", typeof(TimeSpan));   // SQL time
        table.Columns.Add("Avstand", typeof(decimal));
        table.Columns.Add("UpdatedDate_DW", typeof(DateTime));   // SQL datetime
        table.Rows.Add(
            90463406,
            new DateTime(2021, 4, 30, 0, 0, 0, DateTimeKind.Unspecified),
            (byte)31,
            DBNull.Value,
            "10081037",
            58.96265m,
            new TimeSpan(23, 17, 0),
            0.000m,
            new DateTime(2022, 8, 23, 21, 5, 21, DateTimeKind.Unspecified));
        return table;
    }

    private static async Task<byte[]> WriteCsvAsync(DataTable table, CsvWriteOptions options)
    {
        using var reader = table.CreateDataReader();
        using var stream = new MemoryStream();
        await new CsvExportFileWriter(options).WriteAsync(reader, stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task Csv_LegacyValueFormat_RendersValuesTheWayTheLegacyEngineDid()
    {
        using var table = LegacyShape();
        var bytes = await WriteCsvAsync(table, new CsvWriteOptions
        {
            Delimiter = ";",
            Qualifier = '"',
            Encoding = new UTF8Encoding(false),
            ValueFormat = ExportValueFormat.Legacy,
        });

        var lines = Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Byte-for-byte the rendering observed in the live legacy files: invariant-culture MM/dd/yyyy HH:mm:ss
        // for both date and datetime, hh:mm:ss for time, scale-preserving decimals, NULL as an empty field.
        Assert.Equal(
            "90463406;04/30/2021 00:00:00;31;;\"10081037\";58.96265;23:17:00;0.000;08/23/2022 21:05:21",
            lines[1]);
    }

    [Fact]
    public async Task Csv_IsoValueFormat_StaysIso8601()
    {
        using var table = LegacyShape();
        var bytes = await WriteCsvAsync(table, new CsvWriteOptions
        {
            Delimiter = ";",
            Qualifier = '"',
            Encoding = new UTF8Encoding(false),
        });

        var lines = Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "90463406;2021-04-30 00:00:00.0000000;31;;\"10081037\";58.96265;23:17:00;0.000;2022-08-23 21:05:21.0000000",
            lines[1]);
    }

    [Fact]
    public async Task Csv_Utf8Bom_EmitsThePreamble_AndPlainUtf8DoesNot()
    {
        using var withBom = LegacyShape();
        var bom = await WriteCsvAsync(withBom, new CsvWriteOptions
        {
            Delimiter = ";",
            Qualifier = '"',
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        });

        using var without = LegacyShape();
        var plain = await WriteCsvAsync(without, new CsvWriteOptions
        {
            Delimiter = ";",
            Qualifier = '"',
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        });

        Assert.Equal([0xEF, 0xBB, 0xBF], bom.Take(3).ToArray());
        Assert.NotEqual([0xEF, 0xBB, 0xBF], plain.Take(3).ToArray());
    }

    [Theory]
    [InlineData("UTF8BOM", true)]
    [InlineData("utf8bom", true)]
    [InlineData(null, false)]
    [InlineData("UTF8", false)]
    public void WriterFactory_ResolvesTheBomBearingUtf8(string? encoding, bool expectsBom)
    {
        var flow = new ExportFlow
        {
            SysAlias = "e",
            SrcServer = "src",
            Source = new RelationalObject { Database = "Db", Schema = "dbo", Name = "T" },
            TrgEncoding = encoding,
        };

        var writer = Assert.IsType<CsvExportFileWriter>(ExportFileWriterFactory.Create(flow));
        Assert.Equal(expectsBom, writer.Options.Encoding.GetPreamble().Length > 0);
    }

    [Fact]
    public void WriterFactory_CarriesTheFlowsValueFormat()
    {
        var flow = new ExportFlow
        {
            SysAlias = "e",
            SrcServer = "src",
            Source = new RelationalObject { Database = "Db", Schema = "dbo", Name = "T" },
            TrgValueFormat = ExportValueFormat.Legacy,
        };

        var writer = Assert.IsType<CsvExportFileWriter>(ExportFileWriterFactory.Create(flow));
        Assert.Equal(ExportValueFormat.Legacy, writer.Options.ValueFormat);
    }

    [Fact]
    public async Task Parquet_RoundTripsThroughParquetNet()
    {
        using var table = Sample();
        using var reader = table.CreateDataReader();
        var writer = new ParquetExportFileWriter("gzip");

        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);
        Assert.Equal(3, rows);

        stream.Position = 0;
        using var parquet = await ParquetReader.CreateAsync(stream);
        Assert.Equal(3, parquet.Schema.DataFields.Length);
        Assert.Equal(["Id", "Name", "Amount"], parquet.Schema.DataFields.Select(f => f.Name).ToArray());

        using var rowGroup = parquet.OpenRowGroupReader(0);
        var idColumn = await rowGroup.ReadColumnAsync(parquet.Schema.DataFields[0]);
        Assert.Equal(new int?[] { 1, 2, 3 }, idColumn.Data.Cast<int?>().ToArray());

        var nameColumn = await rowGroup.ReadColumnAsync(parquet.Schema.DataFields[1]);
        Assert.Equal(new[] { "Ann", "B;ig\"quote", null }, nameColumn.Data.Cast<string?>().ToArray());
    }
}
