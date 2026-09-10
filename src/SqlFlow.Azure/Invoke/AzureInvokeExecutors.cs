using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Azure.Invoke;

/// <summary>
/// Composition helper that builds the Azure invoke executors (Data Factory + Automation) over one shared
/// service-principal resolver. The composition root (the CLI, or a test) calls this and passes the result to
/// <c>FullModeOptions.InvokeExecutors</c>, so SqlFlow.SqlServer only ever sees the Core
/// <see cref="IInvokeExecutor"/> abstraction and never references SqlFlow.Azure.
/// </summary>
public static class AzureInvokeExecutors
{
    public static IReadOnlyList<IInvokeExecutor> Create(
        IServicePrincipalStore store, ISecretResolver secrets, IAzureCredentialFactory credentialFactory)
    {
        var resolver = new AzureServicePrincipalResolver(store, secrets, credentialFactory);
        return
        [
            new AzureDataFactoryInvokeExecutor(resolver),
            new AzureAutomationInvokeExecutor(resolver),
        ];
    }
}
