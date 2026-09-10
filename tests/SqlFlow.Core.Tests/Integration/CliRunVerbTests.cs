using System.Text.Json;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The CLI's execution verbs end to end against the sink, through the compiled binary: 'plan' renders the SQL
/// without changing anything, 'run' executes a file flow (with --json machine output, --show-sql, and the
/// canonical .sqlflow/runs artifact set), an ingestion flow, an export flow, and a batch of flows in waves,
/// and a run against an unreachable target fails with exit 1 and a FAILED line. This is the "test the core
/// logic without the GUI" loop: a shell script can assert on exit codes and --json exactly like these tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliRunVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_clirun_" + Guid.NewGuid().ToString("N"));
    private readonly string _dll;

    public CliRunVerbTests()
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

    [SkippableFact]
    public async Task Plan_FileFlow_PrintsTheSql_WithoutCreatingTheTable()
    {
        var (dll, cs) = Require();
        var table = "IT_CliPlan_" + Suffix();
        await IntegrationDb.DropTableAsync(cs, table);
        var yaml = WriteCsvFlow("plan", table, "Id,Name\n1,Acme\n");

        var result = await CliBinary.RunAsync(dll, ["plan", yaml], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains(table, result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.False(await IntegrationDb.TableExistsAsync(cs, table), "plan must not create the target table");
    }

    [SkippableFact]
    public async Task Run_FileFlow_Json_ReportsSuccess_AndWritesTheArtifactSet()
    {
        var (dll, cs) = Require();
        var table = "IT_CliJson_" + Suffix();
        await IntegrationDb.DropTableAsync(cs, table);
        var yaml = WriteCsvFlow("json", table, "Id,Name\n1,Acme\n2,Globex\n");

        try
        {
            var result = await CliBinary.RunAsync(dll, ["run", yaml, "--json"], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);

            // --json owns stdout: the whole payload must parse and carry the outcome.
            using var document = JsonDocument.Parse(result.StdOut);
            Assert.Equal("Success", document.RootElement.GetProperty("status").GetString());

            // Every run leaves the canonical artifact set next to the pipeline file.
            var runsRoot = Path.Combine(Path.GetDirectoryName(yaml)!, ".sqlflow", "runs");
            Assert.True(Directory.Exists(runsRoot), "no .sqlflow/runs directory was written");
            var runDir = Directory.GetDirectories(runsRoot, "*", SearchOption.AllDirectories)
                .Single(d => Directory.GetFiles(d, "run.json").Length > 0);
            Assert.True(File.Exists(Path.Combine(runDir, "run.log")), "run.log missing");
            Assert.True(File.Exists(Path.Combine(runDir, "trace.sql")), "trace.sql missing");

            // 'runs local' browses exactly these artifacts: the run must appear, newest first, machine-readable.
            var local = await CliBinary.RunAsync(dll, ["runs", "local", _dir, "--json"], workingDirectory: _dir);
            Assert.True(local.Exit == 0, local.AllOutput);
            using var rows = JsonDocument.Parse(local.StdOut);
            var row = Assert.Single(rows.RootElement.EnumerateArray());
            Assert.Equal(table, row.GetProperty("flowName").GetString());
            Assert.True(row.GetProperty("success").GetBoolean());
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Run_FileFlow_ShowSql_PrintsTheGeneratedStatements()
    {
        var (dll, cs) = Require();
        var table = "IT_CliSql_" + Suffix();
        await IntegrationDb.DropTableAsync(cs, table);
        var yaml = WriteCsvFlow("showsql", table, "Id,Name\n1,Acme\n");

        try
        {
            var result = await CliBinary.RunAsync(
                dll, ["run", yaml, "--show-sql"], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            // The table was dropped up front, so the generated SQL must include the CREATE TABLE DDL.
            Assert.Contains("CREATE TABLE", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Run_IngestionFlow_UpsertsIntoTheTarget()
    {
        var (dll, cs) = Require();
        var suffix = Suffix();
        var src = "IT_CliIngSrc_" + suffix;
        var trg = "IT_CliIngTrg_" + suffix;
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{src}] (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO dbo.[{src}] VALUES (1, N'Acme'), (2, N'Globex');");
        var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
        var yaml = WriteFile("ing.flow.yaml", $$"""
            flowType: ing
            name: cli-ing-{{suffix}}
            connections:
              sink: ${env:SQLFlowSinkConStr}
            source:
              server: sink
              object: {{dbName}}.dbo.{{src}}
            target:
              server: sink
              object: {{dbName}}.dbo.{{trg}}
            load:
              keyColumns: [Id]
            """);

        try
        {
            var result = await CliBinary.RunAsync(dll, ["run", yaml], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Contains("OK", result.StdOut, StringComparison.Ordinal);

            // A second run over unchanged source rows must stay an upsert (still 2 rows), not duplicate.
            var again = await CliBinary.RunAsync(dll, ["run", yaml], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);
            Assert.True(again.Exit == 0, again.AllOutput);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }

    [SkippableFact]
    public async Task Run_ExportFlow_WritesTheCsvFile()
    {
        var (dll, cs) = Require();
        var suffix = Suffix();
        var src = "IT_CliExpSrc_" + suffix;
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE dbo.[{src}] (Id int NOT NULL, Name nvarchar(50) NOT NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO dbo.[{src}] VALUES (1, N'Acme'), (2, N'Globex');");
        var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
        var outDir = Path.Combine(_dir, "export-out");
        Directory.CreateDirectory(outDir);
        var yaml = WriteFile("exp.flow.yaml", $$"""
            flowType: exp
            name: cli-exp-{{suffix}}
            connections:
              sink: ${env:SQLFlowSinkConStr}
            source:
              server: sink
              object: {{dbName}}.dbo.{{src}}
            target:
              path: {{Fwd(outDir)}}
              fileName: out
              addTimestamp: false
              delimiter: "|"
            """);

        try
        {
            var result = await CliBinary.RunAsync(dll, ["run", yaml], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            var file = Assert.Single(Directory.GetFiles(outDir));
            var content = File.ReadAllText(file);
            Assert.Contains("Acme", content, StringComparison.Ordinal);
            Assert.Contains("|", content, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
        }
    }

    [SkippableFact]
    public async Task Run_Batch_RunsEveryMember()
    {
        var (dll, cs) = Require();
        var suffix = Suffix();
        var tableA = "IT_CliBatchA_" + suffix;
        var tableB = "IT_CliBatchB_" + suffix;
        await IntegrationDb.DropTableAsync(cs, tableA);
        await IntegrationDb.DropTableAsync(cs, tableB);
        WriteCsvFlow("member-a", tableA, "Id,Name\n1,Acme\n");
        WriteCsvFlow("member-b", tableB, "Id,Name\n1,Initech\n2,Globex\n");
        var batch = WriteFile("all.batch.yaml", """
            flowType: batch
            name: cli-batch
            members:
              include: ["*.flow.yaml"]
            """);

        try
        {
            var result = await CliBinary.RunAsync(dll, ["run", batch], env: [("SQLFlowSinkConStr", cs)], workingDirectory: _dir);

            Assert.True(result.Exit == 0, result.AllOutput);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, tableA));
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, tableB));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, tableA);
            await IntegrationDb.DropTableAsync(cs, tableB);
        }
    }

    [SkippableFact]
    public async Task Run_UnreachableTarget_Exit1_ReportsFailure()
    {
        var dll = _dll;
        Skip.If(dll.Length == 0, "Built CLI not found; run 'dotnet build -c Release' first.");
        var csv = WriteFile("fail.csv", "Id\n1\n");
        var yaml = WriteFile("fail.flow.yaml", $"""
            name: CliFail
            source:
              type: csv
              location: {Fwd(csv)}
            target:
              connection: "Server=localhost,59999;Database=Nope;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=2"
              schema: dbo
              table: CliFail
            """);

        var result = await CliBinary.RunAsync(dll, ["run", yaml], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("FAILED", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A CSV file flow whose target rides the canonical ${env:SQLFlowSinkConStr} reference, so the
    /// test passes the sink to the child process through the environment exactly as an operator would.</summary>
    private string WriteCsvFlow(string name, string table, string csvContent)
    {
        var csv = WriteFile(name + ".csv", csvContent);
        return WriteFile(name + ".flow.yaml", $$"""
            name: {{table}}
            source:
              type: csv
              location: {{Fwd(csv)}}
            target:
              connection: "${env:SQLFlowSinkConStr}"
              schema: dbo
              table: {{table}}
            """);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string Fwd(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
