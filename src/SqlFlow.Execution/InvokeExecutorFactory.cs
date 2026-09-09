using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Azure;
using SqlFlow.Azure.Invoke;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Execution;

/// <summary>Composes the Azure invoke executors a document's declared invokes need, in the without-database
/// shape: an in-memory service-principal store behind the exact executor pair full mode uses.</summary>
public static class InvokeExecutorFactory
{
    /// <summary>Composes the Azure invoke executors for a document's declared invokes: an in-memory
    /// service-principal store behind the exact executor pair full mode uses. With no declared invokes there is
    /// nothing to execute, so no executor (or Azure credential) is composed at all.</summary>
    public static IReadOnlyList<IInvokeExecutor> Create(
        IServiceProvider provider,
        IReadOnlyList<InvokeDefinition> invokes,
        IReadOnlyList<ServicePrincipalProfile> servicePrincipals)
        => invokes.Count == 0
            ? []
            : AzureInvokeExecutors.Create(
                new InMemoryServicePrincipalStore(servicePrincipals),
                provider.GetRequiredService<ISecretResolver>(),
                provider.GetRequiredService<IAzureCredentialFactory>());
}
