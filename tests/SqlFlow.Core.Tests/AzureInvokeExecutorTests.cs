using SqlFlow.Azure;
using SqlFlow.Azure.Invoke;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Behaviour of the Azure invoke executors that does not touch Azure: type routing and the input/service-principal
/// validation that fails before any management call. Live triggering is covered by the Azure-gated integration
/// tests.
/// </summary>
public sealed class AzureInvokeExecutorTests
{
    private static AzureServicePrincipalResolver LightweightResolver()
        => new(NullServicePrincipalStore.Instance, new SecretResolver([new EnvSecretProvider()]), new AzureCredentialFactory());

    [Fact]
    public void Create_BuildsBothExecutors_RoutingByType()
    {
        var executors = AzureInvokeExecutors.Create(
            NullServicePrincipalStore.Instance, new SecretResolver([new EnvSecretProvider()]), new AzureCredentialFactory());

        Assert.Equal(2, executors.Count);
        Assert.Contains(executors, e => e.CanHandle(InvokeType.AzureDataFactory));
        Assert.Contains(executors, e => e.CanHandle(InvokeType.AzureAutomation));
    }

    [Fact]
    public void DataFactoryExecutor_HandlesOnlyAdf()
    {
        var executor = new AzureDataFactoryInvokeExecutor(LightweightResolver());
        Assert.True(executor.CanHandle(InvokeType.AzureDataFactory));
        Assert.False(executor.CanHandle(InvokeType.AzureAutomation));
    }

    [Fact]
    public void AutomationExecutor_HandlesOnlyAut()
    {
        var executor = new AzureAutomationInvokeExecutor(LightweightResolver());
        Assert.True(executor.CanHandle(InvokeType.AzureAutomation));
        Assert.False(executor.CanHandle(InvokeType.AzureDataFactory));
    }

    [Fact]
    public async Task DataFactory_NoPipelineName_ThrowsBeforeAzure()
    {
        var executor = new AzureDataFactoryInvokeExecutor(LightweightResolver());
        var definition = new InvokeDefinition { FlowId = 1, InvokeAlias = "a", InvokeType = InvokeType.AzureDataFactory };

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => executor.ExecuteAsync(definition));
        Assert.Contains("PipelineName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DataFactory_NoServicePrincipal_ThrowsBeforeAzure()
    {
        var executor = new AzureDataFactoryInvokeExecutor(LightweightResolver());
        var definition = new InvokeDefinition
        {
            FlowId = 1,
            InvokeAlias = "a",
            InvokeType = InvokeType.AzureDataFactory,
            PipelineName = "pl",
        };

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => executor.ExecuteAsync(definition));
        Assert.Contains("service principal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automation_NoRunbookName_ThrowsBeforeAzure()
    {
        var executor = new AzureAutomationInvokeExecutor(LightweightResolver());
        var definition = new InvokeDefinition { FlowId = 1, InvokeAlias = "a", InvokeType = InvokeType.AzureAutomation };

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => executor.ExecuteAsync(definition));
        Assert.Contains("RunbookName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
