using SqlFlow.Core.StoredProcedures;
using SqlFlow.SqlServer.StoredProcedures;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Drives a stored-procedure flow end to end from a YAML document (flowType: sp) through the without-database
/// composition root, exactly as the CLI runs it. Proof of execution is the procedure's side effect (a marker
/// row), plus the always-on SQL trace carrying the EXEC.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YamlStoredProcedureEndToEndTests
{
    private const string Marker = "_SfYmlSp_Marker";
    private const string Proc = "usp_SfYmlSpHook";

    private static readonly YamlStoredProcedureFlowLoader Loader = new();

    private static async Task<StoredProcedureRunResult> RunAsync(string yaml)
    {
        var document = Loader.Parse(yaml);
        var runner = WithoutDatabaseStoredProcedure.BuildRunner(document.Connections);
        return await runner.RunAsync(document.Flow);
    }

    [SkippableFact]
    public async Task YamlStoredProcedure_Executes_AndCapturesTheTrace()
    {
        var cs = IntegrationDb.Require();
        await Reset(cs);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{Marker}] ([RunAt] datetime2(3) NOT NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE OR ALTER PROCEDURE [dbo].[{Proc}] AS INSERT INTO [dbo].[{Marker}] VALUES (SYSUTCDATETIME());");

        try
        {
            var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
            var result = await RunAsync($$"""
                flowType: sp
                name: yaml-sp-e2e
                connections:
                  sink: ${env:SQLFlowSinkConStr}
                procedure:
                  server: sink
                  object: {{dbName}}.dbo.{{Proc}}
                """);

            Assert.True(result.Success, result.Error);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, Marker));

            var trace = Assert.Single(result.SqlTrace);
            Assert.Equal("procedure.exec", trace.Step);
            Assert.Contains(Proc, trace.Sql, StringComparison.Ordinal);
        }
        finally
        {
            await Reset(cs);
        }
    }

    [SkippableFact]
    public async Task YamlStoredProcedure_MissingProcedure_FailsWithTheError()
    {
        var cs = IntegrationDb.Require();
        var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");

        var result = await RunAsync($$"""
            flowType: sp
            name: yaml-sp-missing
            connections:
              sink: ${env:SQLFlowSinkConStr}
            procedure:
              server: sink
              object: {{dbName}}.dbo.usp_SfYmlSp_DoesNotExist
            """);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    private static async Task Reset(string cs)
    {
        await IntegrationDb.ExecuteAsync(cs, $"DROP PROCEDURE IF EXISTS [dbo].[{Proc}];");
        await IntegrationDb.DropTableAsync(cs, Marker);
    }
}
