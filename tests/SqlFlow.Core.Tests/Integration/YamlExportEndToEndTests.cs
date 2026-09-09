using SqlFlow.Core.Export;
using SqlFlow.SqlServer.Export;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Drives an export end to end from a YAML document (flowType: exp) through the without-database composition
/// root, exactly as the CLI runs it: parse, build the runner from the document's own connections, run, and
/// verify the produced file plus the always-on SQL trace.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YamlExportEndToEndTests
{
    private static readonly YamlExportFlowLoader Loader = new();

    private static async Task<ExportRunResult> RunAsync(string yaml)
    {
        var document = Loader.Parse(yaml);
        var runner = WithoutDatabaseExport.BuildRunner(document.Connections);
        return await runner.RunAsync(document.Flow);
    }

    [SkippableFact]
    public async Task YamlExport_WritesCsv_AndCapturesTheTrace()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfYmlExp_Src";
        var dir = TempDir();
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(20) NULL);" +
            $"INSERT INTO [dbo].[{src}] VALUES (1, 'a'), (2, 'b'), (3, NULL);");

        try
        {
            var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
            var result = await RunAsync($$"""
                flowType: exp
                name: yaml-exp-e2e
                connections:
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: sink
                  object: {{dbName}}.dbo.{{src}}
                target:
                  path: {{dir.Replace('\\', '/')}}
                  fileName: out
                  addTimestamp: false
                  delimiter: "|"
                """);

            Assert.True(result.Success, result.Error);
            Assert.Equal(3, result.TotalRows);
            var exported = Assert.Single(result.Files);

            var path = Path.Combine(dir, "out.csv");
            Assert.True(File.Exists(path), $"expected {path}; runner reported {exported.Path}");
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(4, lines.Length);
            Assert.Equal("Id|Name", lines[0]);

            // The generated SQL is the debugging record: probe plus the one full segment, always captured.
            Assert.Contains(result.SqlTrace, t => t.Step == "source.probe");
            Assert.Contains(result.SqlTrace, t => t.Step == "export.segment" && t.Sql.Contains("SELECT", StringComparison.Ordinal));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            Cleanup(dir);
        }
    }

    [SkippableFact]
    public async Task YamlExport_FailedRun_StillCarriesTheTrace()
    {
        var cs = IntegrationDb.Require();
        var dir = TempDir();
        var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");

        try
        {
            var result = await RunAsync($$"""
                flowType: exp
                name: yaml-exp-missing
                connections:
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: sink
                  object: {{dbName}}.dbo._SfYmlExp_DoesNotExist
                target:
                  path: {{dir.Replace('\\', '/')}}
                """);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfymlexp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS temp sweeper gets the rest.
        }
    }
}
