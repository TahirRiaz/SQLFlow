using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

public sealed class InferenceServiceTests
{
    private static InferenceService NewService(IInferenceValidator? validator = null)
        => new(
            new FakeSchema(),
            new FakeProfiler(),
            new TypeInferencer(),
            validator ?? new FakeValidator(),
            new FakeLocaleProvider(),
            new SecretResolver([new EnvSecretProvider()]));

    [Fact]
    public async Task InferAsync_AssemblesReportFromProfiles()
    {
        var request = new InferenceRequest
        {
            Connection = "x",
            Schema = "dbo",
            Table = "Orders",
            Policy = new TypeInferencePolicy { Enabled = true },
        };

        var report = await NewService().InferAsync(request);

        Assert.Equal("Orders", report.Table);
        Assert.Equal(2, report.Columns.Count);
        Assert.Equal("decimal(7, 2)", report.Columns.Single(c => c.ColumnName == "Amount").DataType);
        Assert.Equal("bit", report.Columns.Single(c => c.ColumnName == "IsPaid").DataType);

        // The run binds to the resolved locale and the default fail-loud mode emits CONVERT.
        Assert.Equal("nb-NO", report.Culture);
        Assert.Equal("Fail", report.OnConvertError);

        // The assembled transform SELECT applies each column's conversion and reads from the table.
        Assert.Contains("CONVERT(decimal(7, 2),", report.TransformSelect, StringComparison.Ordinal);
        Assert.Contains("AS [Amount]", report.TransformSelect, StringComparison.Ordinal);
        Assert.Contains("FROM [dbo].[Orders]", report.TransformSelect, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_FlagsSilentNullColumns()
    {
        var request = new InferenceRequest { Connection = "x", Schema = "dbo", Table = "Orders" };
        var service = NewService(new FakeValidator { SilentNulls = { ["Amount"] = 3 } });

        var report = await service.ValidateAsync(request);

        Assert.NotNull(report.Validation);
        Assert.False(report.Validation!.IsValid); // Amount loses values
        var amount = report.Validation.Columns.Single(c => c.ColumnName == "Amount");
        Assert.Equal("lossy", amount.Status);
        Assert.Equal(3, amount.SilentNulls);
        Assert.Equal(70d, amount.FitPercent); // 7 of 10 convert cleanly
        Assert.Equal("ok", report.Validation.Columns.Single(c => c.ColumnName == "IsPaid").Status);
    }

    private sealed class FakeSchema : ISchemaProvider
    {
        public Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default)
            => Task.FromResult<TableSchema?>(new TableSchema
            {
                Schema = schema,
                Table = table,
                Columns =
                [
                    new ColumnDefinition { Name = "Amount", SqlType = "varchar(255)" },
                    new ColumnDefinition { Name = "IsPaid", SqlType = "varchar(255)" },
                ],
            });

        public Task ExecuteDdlAsync(string connectionString, IReadOnlyList<string> statements, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeProfiler : IColumnProfiler
    {
        public Task<IReadOnlyList<ColumnProfile>> ProfileAsync(
            string connectionString, string schema, string table, IReadOnlyList<string> columns,
            TypeInferencePolicy policy, ServerLocale locale, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ColumnProfile>>(
            [
                new ColumnProfile
                {
                    ColumnName = "Amount",
                    Total = 10,
                    NonNull = 10,
                    NumericCandidates =
                    [
                        new NumericConversionCandidate
                        {
                            Format = NumericFormat.Locale,
                            AsDecimal = 10,
                            AsFloat = 10,
                            MaxScale = 2,
                            MaxIntegerDigits = 5,
                        },
                    ],
                },
                new ColumnProfile { ColumnName = "IsPaid", Total = 10, NonNull = 10, AsBitTokens = 10 },
            ]);
    }

    private sealed class FakeLocaleProvider : IServerLocaleProvider
    {
        public Task<ServerLocale> GetServerLocaleAsync(string connectionString, CancellationToken ct = default)
            => Task.FromResult(new ServerLocale
            {
                Culture = "nb-NO",
                DateOrder = DateOrder.Dmy,
                DecimalSeparator = ',',
                GroupSeparator = '.',
            });
    }

    private sealed class FakeValidator : IInferenceValidator
    {
        public Dictionary<string, long> SilentNulls { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<ConversionCheckResult>> CheckAsync(
            string connectionString, string schema, string table, IReadOnlyList<ConversionCheck> checks,
            ServerLocale locale, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversionCheckResult>>(
                checks.Select(c => new ConversionCheckResult
                {
                    ColumnName = c.ColumnName,
                    NonNull = 10,
                    SilentNulls = SilentNulls.GetValueOrDefault(c.ColumnName),
                }).ToList());
    }
}
