using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SqlFlow.Sftp;

/// <summary>
/// Registers the SFTP transfer engine (flowType: sftp) into the SqlFlow engine container: the engine and the flow
/// runner. Assumes the host has already registered <c>ISecretResolver</c> and <c>IAzureCredentialFactory</c> (the
/// shared secret / credential chain), like the other file engines.
/// </summary>
public static class SftpServices
{
    public static IServiceCollection AddSqlFlowSftp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SftpEngine>();
        services.AddSingleton<SftpFlowRunner>();

        return services;
    }
}
