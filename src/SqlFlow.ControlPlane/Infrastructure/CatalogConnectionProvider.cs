using Microsoft.Extensions.Options;
using SqlFlow.Core.Secrets;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// Resolves the catalog connection string once (through the SqlFlow secret resolver, so a connection string,
/// a <c>${env:...}</c>, or a <c>${keyvault:...}</c> reference all work) and caches it for the app lifetime. The
/// resolved string is never logged. A blank resolution is a hard configuration error.
/// </summary>
public sealed class CatalogConnectionProvider
{
    private readonly Lazy<string> _connectionString;

    public CatalogConnectionProvider(ISecretResolver resolver, IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);
        // PublicationOnly so a transient resolution failure (e.g. a Key Vault blip on the first DB-touching
        // request) is NOT cached and re-thrown forever: a later request retries the resolution and can recover
        // without restarting the host. The resolved value, once obtained, is cached for the app lifetime.
        _connectionString = new Lazy<string>(
            () =>
            {
                var resolved = resolver.Resolve(options.Value.Catalog.ConnectionReference);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    throw new InvalidOperationException(
                        "The catalog connection reference resolved to an empty value; set SQLFLOW_CATALOG_DB (or ControlPlane:Catalog:ConnectionReference).");
                }

                return resolved;
            },
            LazyThreadSafetyMode.PublicationOnly);
    }

    public string ConnectionString => _connectionString.Value;
}
