using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Datatype inference and its sanity check against the physical sink: profiles a raw (all-varchar)
/// table, assembles the transform SELECT, and validates the conversions row by row, flagging the
/// column whose non-null values would silently become NULL under the inferred type.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InferenceIntegrationTests
{
    [SkippableFact]
    public async Task Validate_FlagsSilentNulls_AndKeepsCleanColumnsOk()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Infer_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // Raw layer: every column is varchar. Amount is mostly numeric with one bad value; Note is text.
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [dbo].[{table}] ([Amount] varchar(255) NULL, [Note] varchar(255) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{table}] ([Amount],[Note]) VALUES ('1.50','a'),('2.00','b'),('abc','c');");

            var request = new InferenceRequest
            {
                Connection = cs,
                Schema = IntegrationDb.Schema,
                Table = table,
                // Threshold below 1.0 so a numeric type is chosen despite the one bad value, exercising
                // the silent-null path. (At the default 1.0 the column would simply stay a string.)
                Policy = new TypeInferencePolicy { Enabled = true, Threshold = 0.6 },
            };

            var report = await IntegrationDb.RealInferenceService().ValidateAsync(request);

            Assert.Contains("FROM [dbo].[" + table + "]", report.TransformSelect, StringComparison.Ordinal);
            Assert.NotNull(report.Validation);

            var amount = report.Validation!.Columns.Single(c => c.ColumnName == "Amount");
            Assert.Equal("lossy", amount.Status);           // numeric type chosen, but 'abc' fails
            Assert.Equal(3, amount.NonNull);
            Assert.Equal(1, amount.SilentNulls);            // 'abc' would silently null out
            Assert.True(amount.FitPercent is > 66 and < 67);

            var note = report.Validation.Columns.Single(c => c.ColumnName == "Note");
            Assert.Equal("kept-string", note.Status);       // text stays a string, no conversion

            Assert.False(report.Validation.IsValid);        // a lossy column makes the schema invalid
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task NorwegianLocale_ResolvesEveryDateColumnToTheSameStyle_AndParsesCommaDecimals()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Locale_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // Two date columns and a comma-decimal amount, all Norwegian-formatted (dd.mm.yyyy / 1.234,56).
            // One date column is ambiguous (01.02.2026); the other is unambiguously day-first (13.05.2026).
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [dbo].[{table}] ([D1] varchar(50) NULL, [D2] varchar(50) NULL, [Amount] varchar(50) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{table}] ([D1],[D2],[Amount]) VALUES " +
                "('25.12.2026','13.05.2026','1.234,56'),('01.02.2026','30.11.2026','2.000,00');");

            var request = new InferenceRequest
            {
                Connection = cs,
                Schema = IntegrationDb.Schema,
                Table = table,
                // Bind explicitly to nb-NO so the assertion holds regardless of the test server's own locale.
                Policy = new TypeInferencePolicy { Enabled = true, Culture = "nb-NO" },
            };

            var report = await IntegrationDb.RealInferenceService().ValidateAsync(request);

            Assert.Equal("nb-NO", report.Culture);
            Assert.Equal("Dmy", report.DateOrder);

            var d1 = report.Columns.Single(c => c.ColumnName == "D1");
            var d2 = report.Columns.Single(c => c.ColumnName == "D2");
            var amount = report.Columns.Single(c => c.ColumnName == "Amount");

            // The headline fix: both date columns resolve to the SAME style, so the ambiguous date is read
            // the same way as every other date in the file instead of flipping per column.
            Assert.True(d1.Converted);
            Assert.Equal(d2.Style, d1.Style);
            Assert.StartsWith("datetime2", d1.DataType, StringComparison.Ordinal);

            // The comma decimal is recognised (it never was before) and normalized.
            Assert.StartsWith("decimal", amount.DataType, StringComparison.Ordinal);
            Assert.Equal("Locale", amount.NumericFormat);

            // Everything converts cleanly under the bound locale - no silent loss.
            Assert.NotNull(report.Validation);
            Assert.True(report.Validation!.IsValid);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task ShortHyphenCodes_AreNotMistakenForDates()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Code_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // "11-800" and friends carry no 4-digit year; SQL Server's lenient parser would accept them
            // as dates, but the inferencer's date-shape guard must keep the column as a string code.
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [dbo].[{table}] ([Code] varchar(50) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{table}] ([Code]) VALUES ('11-800'),('12-900'),('5-100');");

            var request = new InferenceRequest
            {
                Connection = cs,
                Schema = IntegrationDb.Schema,
                Table = table,
                Policy = new TypeInferencePolicy { Enabled = true },
            };

            var report = await IntegrationDb.RealInferenceService().InferAsync(request);

            var code = report.Columns.Single(c => c.ColumnName == "Code");
            Assert.False(code.Converted);
            Assert.StartsWith("varchar", code.DataType, StringComparison.Ordinal);
            Assert.Null(code.Style);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }
}
