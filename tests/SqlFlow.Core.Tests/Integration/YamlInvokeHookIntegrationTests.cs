using System.Collections.Concurrent;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Drives the YAML invoke hooks end to end through the without-database composition root, exactly as the CLI
/// runs them, with a fake executor in place of Azure: a postInvoke declared under <c>invokes:</c> fires after
/// the load with its typed parameters, and a failing pre-hook with onErrorResume off fails the flow before any
/// data work.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YamlInvokeHookIntegrationTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    private sealed class FakeExecutor : IInvokeExecutor
    {
        public ConcurrentQueue<InvokeDefinition> Executed { get; } = new();

        public bool Fail { get; init; }

        public bool CanHandle(InvokeType type) => type == InvokeType.AzureDataFactory;

        public Task<InvokeExecution> ExecuteAsync(InvokeDefinition definition, CancellationToken ct = default)
        {
            Executed.Enqueue(definition);
            return Fail
                ? throw new SqlFlowException("the pipeline is on fire")
                : Task.FromResult(new InvokeExecution { StandardOutput = "ok" });
        }
    }

    [SkippableFact]
    public async Task PostInvoke_FiresAfterTheLoad_WithTypedParameters()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfYmlInv_Src";
        const string trg = "_SfYmlInv_Trg";
        await Reset(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL); INSERT INTO [dbo].[{src}] VALUES (1), (2);");

        try
        {
            var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
            var document = Loader.Parse($$"""
                flowType: ing
                name: yaml-inv-hook
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
                postInvoke: notify
                servicePrincipals:
                  deploy:
                    subscriptionId: s-1
                    resourceGroup: rg-1
                    dataFactoryName: adf-1
                invokes:
                  notify:
                    type: adf
                    pipeline: pl_notify
                    servicePrincipal: deploy
                    parameters:
                      rows: 2
                      full: true
                """);

            var executor = new FakeExecutor();
            var runner = WithoutDatabaseIngestion.BuildRunner(
                document.Connections, document.AssertionDefinitions,
                invokes: document.Invokes, invokeExecutors: [executor]);

            var result = await runner.RunAsync(document.Flow, new IngestionRunOptions { ExecMode = "test" });

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));

            var executed = Assert.Single(executor.Executed);
            Assert.Equal("notify", executed.InvokeAlias);
            Assert.Equal("pl_notify", executed.PipelineName);
            Assert.Contains("\"rows\":2", executed.ParameterJson, StringComparison.Ordinal);
            Assert.Contains("\"full\":true", executed.ParameterJson, StringComparison.Ordinal);
        }
        finally
        {
            await Reset(cs, src, trg);
        }
    }

    [SkippableFact]
    public async Task FailingPreInvoke_WithResumeOff_FailsTheFlow_BeforeAnyDataWork()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfYmlInvF_Src";
        const string trg = "_SfYmlInvF_Trg";
        await Reset(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL);");

        try
        {
            var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
            var document = Loader.Parse($$"""
                flowType: ing
                name: yaml-inv-prefail
                connections:
                  sink: ${env:SQLFlowSinkConStr}
                source:
                  server: sink
                  object: {{dbName}}.dbo.{{src}}
                target:
                  server: sink
                  object: {{dbName}}.dbo.{{trg}}
                preInvoke: gate
                servicePrincipals:
                  deploy:
                    subscriptionId: s-1
                    resourceGroup: rg-1
                    dataFactoryName: adf-1
                invokes:
                  gate:
                    type: adf
                    pipeline: pl_gate
                    servicePrincipal: deploy
                    onErrorResume: false
                """);

            var runner = WithoutDatabaseIngestion.BuildRunner(
                document.Connections, document.AssertionDefinitions,
                invokes: document.Invokes, invokeExecutors: [new FakeExecutor { Fail = true }]);

            var result = await runner.RunAsync(document.Flow, new IngestionRunOptions { ExecMode = "test" });

            Assert.False(result.Success);
            Assert.Contains("the pipeline is on fire", result.Error, StringComparison.Ordinal);
            Assert.False(await IntegrationDb.TableExistsAsync(cs, trg));   // the gate held: no data work happened
        }
        finally
        {
            await Reset(cs, src, trg);
        }
    }

    private static async Task Reset(string cs, string src, string trg)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
    }
}
