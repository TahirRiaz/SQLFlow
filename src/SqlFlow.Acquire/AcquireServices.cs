using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlFlow.Acquire.Engine;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;

namespace SqlFlow.Acquire;

/// <summary>
/// Registers the generic API-acquisition engine (flowType: api) into the SqlFlow engine container: the raw landing
/// stores (local + Azure, behind a composite selector), the transports (HTTP / SFTP / Azure Table), the auth
/// resolver, the engine, and the flow runner. Assumes the host has already registered <c>ISecretResolver</c>
/// and <c>IAzureCredentialFactory</c> (the shared secret/credential chain) - the engine wiring does.
/// </summary>
public static class AcquireServices
{
    public static IServiceCollection AddSqlFlowAcquire(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IRawLandingStore, LocalRawLandingStore>();
        services.AddSingleton<IRawLandingStore, AzureRawLandingStore>();
        services.AddSingleton<CompositeRawLandingStore>();

        services.AddSingleton<IAcquireTransport, HttpTransport>();
        services.AddSingleton<IAcquireTransport, SftpTransport>();
        services.AddSingleton<IAcquireTransport, AzureTableTransport>();

        services.AddSingleton<AuthResolver>();
        services.AddSingleton(sp => new AcquireEngine(
            sp.GetRequiredService<CompositeRawLandingStore>(),
            sp.GetRequiredService<AuthResolver>(),
            sp.GetRequiredService<SqlFlow.Core.Secrets.ISecretResolver>(),
            sp.GetServices<IAcquireTransport>(),
            sp.GetRequiredService<TimeProvider>(),
            httpClientFactory: null,
            // Optional: only a flow declaring 'incremental.source: sql' needs it, and the host that owns a database
            // provider registers it. Absent, such a flow fails with a message naming what to register.
            watermarkProbe: sp.GetService<SqlFlow.Core.Acquire.IAcquireWatermarkProbe>()));
        services.AddSingleton<AcquireFlowRunner>();

        return services;
    }
}
