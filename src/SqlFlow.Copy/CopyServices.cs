using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlFlow.Copy;

namespace SqlFlow.Copy;

/// <summary>
/// Registers the file-copy engine (flowType: cpy) into the SqlFlow engine container: the storage endpoints (local
/// disk / Azure Blob-ADLS behind the <see cref="ICopyEndpoint"/> selector), the engine, and the flow runner. SFTP is
/// its own flow type (sftp), not a copy endpoint. Assumes the host has already registered <c>ISecretResolver</c> and
/// <c>IAzureCredentialFactory</c> (the shared secret / credential chain), like the other file engines.
/// </summary>
public static class CopyServices
{
    public static IServiceCollection AddSqlFlowCopy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ICopyEndpoint, LocalCopyEndpoint>();
        services.AddSingleton<ICopyEndpoint, AzureBlobCopyEndpoint>();
        services.AddSingleton<ICopyEndpoint, S3CopyEndpoint>();

        services.AddSingleton(sp => new CopyEngine(
            sp.GetServices<ICopyEndpoint>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CopyFlowRunner>();

        return services;
    }
}
