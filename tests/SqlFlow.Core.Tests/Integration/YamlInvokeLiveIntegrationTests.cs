using SqlFlow.Azure;
using SqlFlow.Azure.Invoke;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer.Invoke;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Drives a standalone invoke document (flowType: inv) end to end against live Azure, composed exactly as the
/// CLI composes it: the document's service principals behind an in-memory store, the Azure executor pair, the
/// without-database invoke runner. Gated on the SQLFLOW_AZTEST_* environment (skips otherwise); the client
/// secret reaches the credential only through the ${env:...} reference in the document.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YamlInvokeLiveIntegrationTests
{
    [SkippableFact]
    public async Task YamlInvoke_TriggersTheAdfPipeline_AndWaits()
    {
        var config = AzureTestEnv.RequireDataFactory();

        var document = new YamlInvokeFlowLoader().Parse($$"""
            flowType: inv
            name: yaml-inv-live
            servicePrincipals:
              live:
                tenantId: {{config.Tenant}}
                clientId: {{config.ClientId}}
                clientSecret: ${env:SQLFLOW_AZTEST_CLIENT_SECRET}
                subscriptionId: {{config.Subscription}}
                resourceGroup: {{config.ResourceGroup}}
                dataFactoryName: {{config.DataFactory}}
            invoke:
              type: adf
              pipeline: {{config.Pipeline}}
              servicePrincipal: live
            """);

        var runner = WithoutDatabaseInvoke.BuildRunner(AzureInvokeExecutors.Create(
            new InMemoryServicePrincipalStore(document.ServicePrincipals),
            new SecretResolver([new EnvSecretProvider()]),
            new AzureCredentialFactory()));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var result = await runner.RunAsync(document.Definition, ct: cts.Token);

        Assert.True(result.Success, result.Error);
        Assert.Contains("status=Succeeded", result.StandardOutput, StringComparison.Ordinal);
    }
}
