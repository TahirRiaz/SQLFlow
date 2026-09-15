using System.Collections.Concurrent;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Catalog.Modules;

/// <summary>Resolves the connection string of a module database in a host. Every host registers one.</summary>
public interface IModuleDatabaseConnections
{
    /// <summary>The connection string of <paramref name="database"/>: its own reference resolved, or the host's catalog connection.</summary>
    /// <exception cref="ModuleDatabaseException">The module uses the catalog connection and the host has none, or its reference resolved to nothing.</exception>
    string ConnectionString(ModuleDatabase database);
}

/// <summary>
/// The one implementation of <see cref="IModuleDatabaseConnections"/>: a module's own reference is resolved through the
/// host's secret resolver, the catalog connection comes from the host. A resolved connection is cached for the host's
/// lifetime; a failed resolution is not, so a later call can recover from a transient secret store failure.
/// </summary>
public sealed class ModuleDatabaseConnections : IModuleDatabaseConnections
{
    private readonly ISecretResolver _secrets;
    private readonly Func<string>? _catalogConnection;
    private readonly string? _noCatalogReason;
    // Keyed by the declaration itself, not its module name: a resolved connection belongs to the reference it was resolved from.
    private readonly ConcurrentDictionary<ModuleDatabase, Lazy<string>> _resolved = new(ReferenceEqualityComparer.Instance);

    /// <param name="secrets">The host's secret resolver.</param>
    /// <param name="catalogConnection">The host's resolved catalog connection string, read when a module on it is first opened.</param>
    public ModuleDatabaseConnections(ISecretResolver secrets, Func<string> catalogConnection)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(catalogConnection);
        _secrets = secrets;
        _catalogConnection = catalogConnection;
    }

    private ModuleDatabaseConnections(ISecretResolver secrets, string noCatalogReason)
    {
        _secrets = secrets;
        _noCatalogReason = noCatalogReason;
    }

    /// <summary>Connections for a host with no catalog connection (a compute node): only modules with their own reference open.</summary>
    /// <param name="secrets">The host's secret resolver.</param>
    /// <param name="reason">Why the host has no catalog connection, completing "but ...", for example "a worker node has no catalog connection".</param>
    public static ModuleDatabaseConnections WithoutCatalog(ISecretResolver secrets, string reason)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ModuleDatabaseConnections(secrets, reason);
    }

    public string ConnectionString(ModuleDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return _resolved
            .GetOrAdd(database, key => new Lazy<string>(() => Resolve(key), LazyThreadSafetyMode.PublicationOnly))
            .Value;
    }

    private string Resolve(ModuleDatabase database)
    {
        if (database.ConnectionReference is null)
        {
            if (_catalogConnection is null)
            {
                throw new ModuleDatabaseException(
                    database.Module,
                    $"The database of module '{database.Module}' uses the catalog connection, but {_noCatalogReason}. Give the module database a connection reference of its own.");
            }

            return _catalogConnection();
        }

        var resolved = _secrets.Resolve(database.ConnectionReference);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new ModuleDatabaseException(
                database.Module, $"The connection reference of module '{database.Module}' resolved to an empty value.");
        }

        return resolved;
    }
}
