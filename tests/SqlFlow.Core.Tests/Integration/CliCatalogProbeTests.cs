using System.Text.Json;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The CLI's source-discovery and probing verbs against a live SQL Server, through the compiled binary:
/// 'catalog' listing (databases, tables, columns, search), 'catalog scaffold' producing a runnable ingestion
/// flow (proven runnable by feeding it straight back to 'validate'), 'detect-unique-key' answering from
/// declared metadata (exit 0) and reporting an unkeyable table (exit 2), and the zero-configuration
/// 'healthcheck' scoring a real series with --json machine output.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliCatalogProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_clicat_" + Guid.NewGuid().ToString("N"));
    private readonly string _dll;

    public CliCatalogProbeTests()
    {
        Directory.CreateDirectory(_dir);
        _dll = CliBinary.DllPath() ?? string.Empty;
    }

    private (string Dll, string Cs) Require()
    {
        var cs = IntegrationDb.Require();
        Skip.If(_dll.Length == 0, "Built CLI not found; run 'dotnet build -c Release' first.");
        return (_dll, cs);
    }

    private static (string Name, string? Value)[] SourceEnv(string cs) => [("SQLFLOW_CLITEST_SRC", cs)];

    [SkippableFact]
    public async Task Catalog_Tables_ListsTheSeededTable()
    {
        var (dll, cs) = Require();
        var table = "IT_CliCatT_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL);");
        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["catalog", "tables", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--like", table],
                env: SourceEnv(cs), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains(table, result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Catalog_Columns_ListsTheColumnsWithTypes()
    {
        var (dll, cs) = Require();
        var table = "IT_CliCatC_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (Id int NOT NULL PRIMARY KEY, CustomerName nvarchar(50) NULL, CreatedAt datetime2 NOT NULL);");
        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["catalog", "columns", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--object", "dbo." + table],
                env: SourceEnv(cs), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains("CustomerName", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("CreatedAt", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Catalog_Search_FindsTheTableByTerm()
    {
        var (dll, cs) = Require();
        var table = "IT_CliCatS_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (Id int NOT NULL);");
        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["catalog", "search", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--term", table],
                env: SourceEnv(cs), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains(table, result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Catalog_Databases_ListsTheCurrentDatabase()
    {
        var (dll, cs) = Require();
        var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");

        var result = await CliBinary.RunAsync(
            dll, ["catalog", "databases", "--source", "${env:SQLFLOW_CLITEST_SRC}"],
            env: SourceEnv(cs), workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains(dbName!, result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Catalog_Scaffold_WritesARunnableIngestionFlow()
    {
        var (dll, cs) = Require();
        var table = "IT_CliScaf_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL);");
        var outPath = Path.Combine(_dir, "scaffolded.flow.yaml");
        try
        {
            var scaffold = await CliBinary.RunAsync(
                dll,
                [
                    "catalog", "scaffold", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--object", "dbo." + table,
                    "--target", "${env:SQLFLOW_CLITEST_SRC}", "--target-object", "raw." + table, "-o", outPath,
                ],
                env: SourceEnv(cs), workingDirectory: _dir);

            Assert.True(scaffold.Exit == 0, scaffold.AllOutput);
            var yaml = File.ReadAllText(outPath);
            Assert.Contains("flowType: ing", yaml, StringComparison.Ordinal);
            // The declared primary key must arrive as the load key, and the secret must stay a reference.
            Assert.Contains("Id", yaml, StringComparison.Ordinal);
            Assert.Contains("${env:SQLFLOW_CLITEST_SRC}", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain(cs, yaml, StringComparison.OrdinalIgnoreCase);

            // "Runnable" is proven by the CLI's own validator, not by eyeballing the YAML.
            var validate = await CliBinary.RunAsync(dll, ["validate", outPath], env: SourceEnv(cs), workingDirectory: _dir);
            Assert.True(validate.Exit == 0, validate.AllOutput);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DetectUniqueKey_DeclaredPrimaryKey_AnsweredFromMetadata_Exit0()
    {
        var (dll, cs) = Require();
        var table = "IT_CliKey_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (OrderId int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO dbo.[{table}] VALUES (1, N'a'), (2, N'b');");
        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["detect-unique-key", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--object", "dbo." + table],
                env: SourceEnv(cs), workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Contains("OrderId", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DetectUniqueKey_NoUniqueColumnSet_Exit2()
    {
        var (dll, cs) = Require();
        var table = "IT_CliNoKey_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (A int NOT NULL, B int NOT NULL);");
        // Two identical rows make every column set non-unique, so the search must come up empty.
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO dbo.[{table}] VALUES (1, 1), (1, 1);");
        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["detect-unique-key", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--object", "dbo." + table],
                env: SourceEnv(cs), workingDirectory: _dir);

            Assert.Equal(2, result.Exit);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Healthcheck_AdHoc_ScoresARealSeries_JsonExit0()
    {
        var (dll, cs) = Require();
        var table = "IT_CliHc_" + Suffix();
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{table}] (OrderDate date NOT NULL, Amount int NOT NULL);");
        // A 120-day series (the span the engine's own health-check suite uses) whose daily SUM varies by
        // weekday plus a small deterministic wiggle: a zero-variance series gives AutoML nothing to fit (every
        // trial fails), so the monitored metric must actually move.
        await IntegrationDb.ExecuteAsync(cs, $"""
            ;WITH days AS (
                SELECT TOP (120) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.objects a CROSS JOIN sys.objects b)
            INSERT INTO dbo.[{table}] (OrderDate, Amount)
            SELECT DATEADD(day, -n, CAST(GETUTCDATE() AS date)),
                   100 + DATEPART(weekday, DATEADD(day, -n, CAST(GETUTCDATE() AS date))) * 10 + n % 7
            FROM days CROSS JOIN (VALUES (1), (2), (3)) AS v(val);
            """);
        try
        {
            var result = await CliBinary.RunAsync(
                dll,
                [
                    "healthcheck", "--source", "${env:SQLFLOW_CLITEST_SRC}", "--object", "dbo." + table,
                    "--base-value", "SUM(Amount)", "--budget", "8", "--json",
                ],
                env: SourceEnv(cs), workingDirectory: _dir, timeoutSeconds: 300);

            Assert.True(result.Exit == 0, result.AllOutput);
            using var document = JsonDocument.Parse(result.StdOut);
            Assert.True(document.RootElement.GetProperty("result").GetProperty("success").GetBoolean(), result.StdOut);
            // The auto-detected date column is reported as a note on stderr, keeping stdout pure JSON.
            Assert.Contains("OrderDate", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
