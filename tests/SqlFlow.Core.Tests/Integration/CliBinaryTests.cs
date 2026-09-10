using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the actual compiled CLI binary (not just the library): argument parsing, the DI
/// composition root, exit codes, and a real end-to-end run against the sink. Skips when the built CLI
/// or the sink is unavailable, so the default unit run is unaffected. The broader per-verb suites live in
/// <see cref="CliOfflineVerbTests"/>, <see cref="CliRunVerbTests"/>, <see cref="CliCatalogProbeTests"/>, and
/// <see cref="CliCatalogDbVerbTests"/>; this file keeps the two original smoke paths.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliBinaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_cli_" + Guid.NewGuid().ToString("N"));

    public CliBinaryTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task Cli_Validate_ParsesAndExitsZero()
    {
        var dll = CliBinary.DllPath();
        Skip.If(dll is null, "Built CLI not found; run 'dotnet build -c Release' first.");

        var csv = Path.Combine(_dir, "orders.csv");
        File.WriteAllText(csv, "OrderId,Customer\n1,Acme\n");
        var yaml = WriteYaml("cli_validate", csv, "Cli_Validate", "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True");

        var result = await CliBinary.RunAsync(dll!, ["validate", yaml], workingDirectory: _dir);

        Assert.True(result.Exit == 0, $"validate exit={result.Exit}, stderr={result.StdErr}");
        Assert.Contains("OK", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Cli_Run_LoadsTableAgainstSink()
    {
        var cs = IntegrationDb.Require();
        var dll = CliBinary.DllPath();
        Skip.If(dll is null, "Built CLI not found; run 'dotnet build -c Release' first.");

        var table = "IT_Cli_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var csv = Path.Combine(_dir, "orders.csv");
        File.WriteAllText(csv, "OrderId,Customer\n1,Acme\n2,Globex\n3,Initech\n");
        var yaml = WriteYaml("cli_run", csv, table, "${env:SQLFlowSinkConStr}");

        try
        {
            var result = await CliBinary.RunAsync(
                dll!, ["run", yaml], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

            Assert.True(result.Exit == 0, $"run exit={result.Exit}, output={result.AllOutput}");
            Assert.True(await IntegrationDb.TableExistsAsync(cs, table));
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private string WriteYaml(string name, string csvPath, string table, string connection)
    {
        var path = Path.Combine(_dir, name + ".flow.yaml");
        var yaml = $"""
            name: {table}
            source:
              type: csv
              location: {csvPath.Replace("\\", "/")}
            target:
              connection: "{connection}"
              schema: dbo
              table: {table}
            """;
        File.WriteAllText(path, yaml);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
