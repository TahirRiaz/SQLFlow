using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration.Oracle;

/// <summary>
/// Value fidelity across the full Oracle-to-SQL-Server ingestion path: each test seeds one typed column with a
/// boundary or tricky value, runs a real ingestion flow, and asserts the value survived into the sink with the
/// mapped type. Gated on both <c>SQLFLOW_TEST_ORACLE</c> (source) and the SQL Server sink used by the rest of
/// the integration suite.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OracleIngestionFidelityTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    [SkippableFact]
    public async Task Decimal_PreservesPrecisionAndSign()
        => Assert.Equal(-1234.5678m, await RoundTripAsync<decimal?>("NUMBER(18,4)", "-1234.5678"));

    [SkippableFact]
    public async Task Integer_PreservesLargeValue()
        => Assert.Equal(9223372036854775807m, await RoundTripAsync<decimal?>("INTEGER", "9223372036854775807"));

    [SkippableFact]
    public async Task Unicode_Survives()
        => Assert.Equal("Ω你好", await RoundTripAsync<string?>("NVARCHAR2(50)", "N'Ω你好'"));

    [SkippableFact]
    public async Task Null_Survives()
        => Assert.Null(await RoundTripAsync<decimal?>("NUMBER(10,2)", "NULL"));

    [SkippableFact]
    public async Task Clob_SurvivesLongText()
    {
        var text = new string('a', 3000);
        Assert.Equal(text, await RoundTripAsync<string?>("CLOB", $"'{text}'"));
    }

    [SkippableFact]
    public async Task Raw_SurvivesAsBinary()
    {
        var bytes = await RoundTripAsync<byte[]?>("RAW(4)", "HEXTORAW('DEADBEEF')");
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, bytes);
    }

    [SkippableFact]
    public async Task Timestamp_SurvivesToMillisecond()
        => Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, 123),
            await RoundTripAsync<DateTime?>("TIMESTAMP(3)", "TIMESTAMP '2024-05-06 07:08:09.123'"));

    [SkippableFact]
    public async Task Date_SurvivesDayAndTime()
        => Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9),
            await RoundTripAsync<DateTime?>("DATE", "TO_DATE('2024-05-06 07:08:09', 'YYYY-MM-DD HH24:MI:SS')"));

    /// <summary>Seeds an Oracle table with (id=1, v=&lt;valueLiteral&gt;), ingests it, and returns the sink's
    /// value of column V for that row.</summary>
    private static async Task<T?> RoundTripAsync<T>(string valueType, string valueLiteral)
    {
        var sink = IntegrationDb.Require();
        var oracle = ForeignDb.Require(DataSourceKind.Oracle);

        var trg = "_SfOraFid_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(sink, trg);

        await using var connection = await ForeignDb.OpenAsync(DataSourceKind.Oracle, oracle);
        var schema = await OracleTestSupport.CurrentSchemaAsync(connection);
        var table = OracleTestSupport.Unique("SF_FID");
        await OracleTestSupport.DropTableAsync(connection, table);
        await OracleTestSupport.ExecAsync(connection, $"CREATE TABLE {table} (id NUMBER(9) PRIMARY KEY, v {valueType})");
        await OracleTestSupport.ExecAsync(connection, $"INSERT INTO {table} (id, v) VALUES (1, {valueLiteral})");

        try
        {
            var yaml = $$"""
                flowType: ing
                name: oracle-fidelity
                connections:
                  erp:
                    provider: oracle
                    connection: ${env:SQLFLOW_TEST_ORACLE}
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: erp
                  object: db.{{schema}}.{{table}}
                target:
                  server: sink
                  object: db.dbo.{{trg}}
                load:
                  keyColumns: [ID]
                """;

            var result = await RunAsync(yaml);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(sink, trg));

            return await IntegrationDb.ScalarAsync<T>(sink, $"SELECT [v] FROM [dbo].[{trg}] WHERE [id] = 1");
        }
        finally
        {
            await OracleTestSupport.DropTableAsync(connection, table);
            await IntegrationDb.DropTableAsync(sink, trg);
        }
    }

    private static async Task<IngestionRunResult> RunAsync(string yaml)
    {
        var document = Loader.Parse(yaml);
        var runner = WithoutDatabaseIngestion.BuildRunner(
            document.Connections, document.AssertionDefinitions, providers: SqlFlowSourceProviders.CreateRegistry());
        return await runner.RunAsync(document.Flow, new IngestionRunOptions { ExecMode = "test" });
    }
}
