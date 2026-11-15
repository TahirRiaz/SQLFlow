using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Dataverse;
using Xunit;

namespace SqlFlow.Tests.Dataverse;

/// <summary>
/// One Web API row into typed cells: each kind reads the encodings the service uses, a 64-bit row version stays exact,
/// absent and null are both a null cell, and a value that cannot be its column's type fails naming the row and column.
/// </summary>
public sealed class DataverseValuesTests
{
    private static object Convert(string json, DataverseValueKind kind)
    {
        using var document = JsonDocument.Parse(json);
        return DataverseValues.Convert(document.RootElement, kind);
    }

    [Fact]
    public void Each_kind_reads_the_encodings_the_service_writes()
    {
        Assert.Equal(new Guid("5c1f2a8e-0000-0000-0000-000000000001"), Convert("\"5c1f2a8e-0000-0000-0000-000000000001\"", DataverseValueKind.Guid));
        Assert.Equal("2,3,8,9", Convert("\"2,3,8,9\"", DataverseValueKind.Text));
        Assert.Equal(42, Convert("42", DataverseValueKind.Integer));
        Assert.Equal(42, Convert("\"42\"", DataverseValueKind.Integer));
        Assert.Equal(20000.0000m, Convert("20000.0000", DataverseValueKind.Decimal));
        Assert.Equal(20000.5m, Convert("\"20000.5\"", DataverseValueKind.Decimal));
        Assert.Equal(0.25d, Convert("0.25", DataverseValueKind.Double));
        Assert.Equal(true, Convert("true", DataverseValueKind.Boolean));
        Assert.Equal(new DateTime(2023, 4, 7, 21, 59, 1, DateTimeKind.Utc), Convert("\"2023-04-07T21:59:01Z\"", DataverseValueKind.DateTime));
        Assert.Equal(new DateTime(2024, 5, 1), Convert("\"2024-05-01\"", DataverseValueKind.DateOnly));
        Assert.Equal(new DateTime(2024, 5, 1), Convert("\"2024-05-01T00:00:00Z\"", DataverseValueKind.DateOnly));
        Assert.Equal("{\"Value\":true}", Convert("{\"Value\":true}", DataverseValueKind.Raw));
    }

    [Fact]
    public void A_row_version_past_two_to_the_53rd_stays_exact()
    {
        Assert.Equal(638_183_964_640_916_097L, Convert("638183964640916097", DataverseValueKind.BigInt));
        Assert.Equal(long.MaxValue, Convert("\"9223372036854775807\"", DataverseValueKind.BigInt));
    }

    [Fact]
    public void A_date_and_time_with_an_offset_is_kept_in_utc()
        => Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), Convert("\"2026-11-01T10:00:00+02:00\"", DataverseValueKind.DateTime));

    [Fact]
    public void Absent_and_null_properties_are_null_cells()
    {
        using var row = JsonDocument.Parse("""{"accountid":"5c1f2a8e-0000-0000-0000-000000000001","name":null}""");
        var columns = new[]
        {
            Column("accountid", DataverseValueKind.Guid),
            Column("name", DataverseValueKind.Text),
            Column("revenue", DataverseValueKind.Decimal),
        };

        var cells = DataverseValues.Row(row.RootElement, columns, "account", "accountid");

        Assert.NotNull(cells[0]);
        Assert.Null(cells[1]);
        Assert.Null(cells[2]);
    }

    [Theory]
    [InlineData("numberofemployees", "Integer", "\"many\"")]
    [InlineData("numberofemployees", "Integer", "4294967296")]
    [InlineData("accountid2", "Guid", "\"not-a-guid\"")]
    [InlineData("modifiedon", "DateTime", "17")]
    [InlineData("flag", "Boolean", "\"perhaps\"")]
    [InlineData("score", "Double", "\"NaN\"")]
    public void A_value_that_cannot_be_its_columns_type_fails_naming_the_row_and_column(string name, string kind, string value)
    {
        using var row = JsonDocument.Parse($$"""{"accountid":"5c1f2a8e-0000-0000-0000-000000000001","{{name}}":{{value}}}""");

        var ex = Assert.Throws<SqlFlowException>(() =>
            DataverseValues.Row(row.RootElement, [Column(name, Enum.Parse<DataverseValueKind>(kind))], "account", "accountid"));

        Assert.Contains("table 'account', row 5c1f2a8e-0000-0000-0000-000000000001", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"column '{name}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_bad_value_is_quoted_only_in_part()
    {
        var huge = new string('x', 5000);
        using var row = JsonDocument.Parse($$"""{"accountid":"5c1f2a8e-0000-0000-0000-000000000001","n":"{{huge}}"}""");

        var ex = Assert.Throws<SqlFlowException>(() => DataverseValues.Row(row.RootElement, [Column("n", DataverseValueKind.Integer)], "account", "accountid"));
        Assert.True(ex.Message.Length < 1000, ex.Message);
    }

    private static DataverseColumn Column(string name, DataverseValueKind kind) => new()
    {
        Name = name,
        Property = name,
        Kind = kind,
        ClrType = typeof(object),
        SqlType = "SQL_VARIANT",
        SelectName = name,
        AttributeType = kind.ToString(),
    };
}
