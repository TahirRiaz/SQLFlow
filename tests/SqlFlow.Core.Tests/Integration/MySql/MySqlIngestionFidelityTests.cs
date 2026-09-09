using MySqlConnector;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Providers;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration.MySql;

/// <summary>
/// Value fidelity across the full MySQL-to-SQL-Server ingestion path: each test seeds one typed column with a
/// boundary or tricky value, runs a real ingestion flow, and asserts the value survived into the sink with the
/// mapped type. Gated on both <c>SQLFLOW_TEST_MYSQL</c> (source) and the SQL Server sink used by the rest of
/// the integration suite. In this engine a schema IS a database, so the ingestion YAML addresses the source as
/// <c>db.&lt;database&gt;.&lt;table&gt;</c> where the database is the one named in <c>SQLFLOW_TEST_MYSQL</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlIngestionFidelityTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    [SkippableFact]
    public async Task Decimal_PreservesPrecisionAndSign()
        => Assert.Equal(-1234.5678m, await RoundTripAsync<decimal?>("DECIMAL(18,4)", "-1234.5678"));

    [SkippableFact]
    public async Task BigUnsigned_PreservesMaxValue()
        => Assert.Equal(18446744073709551615m, await RoundTripAsync<decimal?>("BIGINT UNSIGNED", "18446744073709551615"));

    [SkippableFact]
    public async Task Unicode_Survives()
        => Assert.Equal("Ω你好", await RoundTripAsync<string?>("VARCHAR(50) CHARACTER SET utf8mb4", "'Ω你好'"));

    [SkippableFact]
    public async Task Null_Survives()
        => Assert.Null(await RoundTripAsync<decimal?>("DECIMAL(10,2)", "NULL"));

    [SkippableFact]
    public async Task Text_SurvivesLongText()
    {
        var text = new string('a', 3000);
        Assert.Equal(text, await RoundTripAsync<string?>("TEXT", $"'{text}'"));
    }

    [SkippableFact]
    public async Task Varbinary_SurvivesAsBinary()
    {
        var bytes = await RoundTripAsync<byte[]?>("VARBINARY(4)", "x'DEADBEEF'");
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, bytes);
    }

    // CHAR(36) is MySQL's UUID idiom, but MySQL has no UUID type: the value is text and lands as the nchar the
    // type mapper declared. Reinterpreting it as a CLR Guid failed the bulk copy into that column outright, and
    // a CHAR(36) holding anything but a GUID (second case) never parsed at all.
    [SkippableFact]
    public async Task Char36_Uuid_SurvivesAsText()
        => Assert.Equal("2828ca9b-fd72-11ea-80a9-42010a1b3007",
            await RoundTripAsync<string?>("CHAR(36)", "'2828ca9b-fd72-11ea-80a9-42010a1b3007'"));

    [SkippableFact]
    public async Task Char36_NonGuid_SurvivesAsText()
        => Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            await RoundTripAsync<string?>("CHAR(36)", "'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'"));

    [SkippableFact]
    public async Task Datetime_SurvivesToMillisecond()
        => Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, 123),
            await RoundTripAsync<DateTime?>("DATETIME(3)", "'2024-05-06 07:08:09.123'"));

    [SkippableFact]
    public async Task Date_SurvivesDay()
        => Assert.Equal(new DateTime(2024, 5, 6),
            await RoundTripAsync<DateTime?>("DATE", "'2024-05-06'"));

    /// <summary>Seeds a MySQL table with (id=1, v=&lt;valueLiteral&gt;), ingests it, and returns the sink's
    /// value of column v for that row.</summary>
    private static async Task<T?> RoundTripAsync<T>(string valueType, string valueLiteral)
    {
        var sink = IntegrationDb.Require();
        var mysql = ForeignDb.Require(DataSourceKind.MySQL);

        var trg = "_SfMyFid_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(sink, trg);

        await using var connection = await ForeignDb.OpenAsync(DataSourceKind.MySQL, mysql);
        var database = new MySqlConnectionStringBuilder(mysql).Database;
        var table = MySqlTestSupport.Unique("sf_fid");
        await MySqlTestSupport.DropTableAsync(connection, table);
        await MySqlTestSupport.ExecAsync(connection, $"CREATE TABLE `{table}` (id INT PRIMARY KEY, v {valueType})");
        await MySqlTestSupport.ExecAsync(connection, $"INSERT INTO `{table}` (id, v) VALUES (1, {valueLiteral})");

        try
        {
            var yaml = $$"""
                flowType: ing
                name: mysql-fidelity
                connections:
                  shop:
                    provider: mysql
                    connection: ${env:SQLFLOW_TEST_MYSQL}
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: shop
                  object: db.{{database}}.{{table}}
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
            await MySqlTestSupport.DropTableAsync(connection, table);
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
