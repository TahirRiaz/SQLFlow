using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration.Postgres;

/// <summary>
/// Value fidelity across the full PostgreSQL-to-SQL-Server ingestion path: each test seeds one typed column with
/// a boundary or tricky value, runs a real ingestion flow, and asserts the value survived into the sink with the
/// mapped type. Gated on both <c>SQLFLOW_TEST_PG</c> (source) and the SQL Server sink used by the rest of the
/// integration suite. Unquoted PostgreSQL identifiers fold to lower case, so the seeded columns are <c>id</c>
/// and <c>v</c> in both source and sink.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresIngestionFidelityTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    [SkippableFact]
    public async Task Decimal_PreservesPrecisionAndSign()
        => Assert.Equal(-1234.5678m, await RoundTripAsync<decimal?>("numeric(18,4)", "-1234.5678"));

    [SkippableFact]
    public async Task Unicode_Survives()
        => Assert.Equal("Ω你好", await RoundTripAsync<string?>("varchar(50)", "'Ω你好'"));

    [SkippableFact]
    public async Task Null_Survives()
        => Assert.Null(await RoundTripAsync<decimal?>("numeric(10,2)", "NULL"));

    [SkippableFact]
    public async Task Text_SurvivesLongText()
    {
        var text = new string('a', 3000);
        Assert.Equal(text, await RoundTripAsync<string?>("text", $"'{text}'"));
    }

    [SkippableFact]
    public async Task Uuid_Survives()
        => Assert.Equal(Guid.Parse("a7be9d57-1111-2222-3333-444444444444"),
            await RoundTripAsync<Guid?>("uuid", "'a7be9d57-1111-2222-3333-444444444444'"));

    [SkippableFact]
    public async Task Bytea_SurvivesAsBinary()
    {
        var bytes = await RoundTripAsync<byte[]?>("bytea", "'\\xDEADBEEF'::bytea");
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, bytes);
    }

    [SkippableFact]
    public async Task Timestamptz_SurvivesToMillisecond()
    {
        // timestamptz(3) maps to datetimeoffset(3); the seeded UTC instant reads back as that offset.
        var value = await RoundTripAsync<DateTimeOffset?>("timestamptz(3)", "'2024-05-06 07:08:09.123+00'");
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, 123, TimeSpan.Zero), value!.Value.ToUniversalTime());
    }

    [SkippableFact]
    public async Task Timestamp_SurvivesToMillisecond()
        => Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, 123),
            await RoundTripAsync<DateTime?>("timestamp(3)", "'2024-05-06 07:08:09.123'"));

    /// <summary>Seeds a PostgreSQL table with (id=1, v=&lt;valueLiteral&gt;), ingests it, and returns the sink's
    /// value of column v for that row.</summary>
    private static async Task<T?> RoundTripAsync<T>(string valueType, string valueLiteral)
    {
        var sink = IntegrationDb.Require();
        var pg = ForeignDb.Require(DataSourceKind.PostgreSQL);

        var trg = "_SfPgFid_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(sink, trg);

        await using var connection = await ForeignDb.OpenAsync(DataSourceKind.PostgreSQL, pg);
        var table = PostgresTestSupport.Unique("sf_fid");
        await PostgresTestSupport.DropTableAsync(connection, table);
        await PostgresTestSupport.ExecAsync(connection, $"CREATE TABLE public.{table} (id integer PRIMARY KEY, v {valueType})");
        await PostgresTestSupport.ExecAsync(connection, $"INSERT INTO public.{table} (id, v) VALUES (1, {valueLiteral})");

        try
        {
            var yaml = $$"""
                flowType: ing
                name: postgres-fidelity
                connections:
                  erp:
                    provider: postgres
                    connection: ${env:SQLFLOW_TEST_PG}
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: erp
                  object: db.public.{{table}}
                target:
                  server: sink
                  object: db.dbo.{{trg}}
                load:
                  keyColumns: [id]
                """;

            var result = await RunAsync(yaml);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(sink, trg));

            return await IntegrationDb.ScalarAsync<T>(sink, $"SELECT [v] FROM [dbo].[{trg}] WHERE [id] = 1");
        }
        finally
        {
            await PostgresTestSupport.DropTableAsync(connection, table);
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
