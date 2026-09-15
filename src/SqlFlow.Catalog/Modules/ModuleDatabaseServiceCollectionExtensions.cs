using Microsoft.Extensions.DependencyInjection;

namespace SqlFlow.Catalog.Modules;

/// <summary>Registers module databases into a host's services. The host module contracts call it for their modules.</summary>
public static class ModuleDatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers <paramref name="database"/>: as <see cref="ModuleDatabase"/> (what the hosts migrate, report and verify), as its
    /// own <see cref="ModuleDatabase{TContext}"/>, and as an <c>IDbContextFactory</c> of its context on the connection the host's
    /// <see cref="IModuleDatabaseConnections"/> resolves.
    /// </summary>
    /// <exception cref="ModuleDatabaseException">Another module database already has the module's name or its schema.</exception>
    public static IServiceCollection AddModuleDatabase(this IServiceCollection services, ModuleDatabase database)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ModuleDatabase) || descriptor.ImplementationInstance is not ModuleDatabase existing)
            {
                continue;
            }

            if (string.Equals(existing.Module, database.Module, StringComparison.Ordinal))
            {
                throw new ModuleDatabaseException(database.Module, $"The database of module '{database.Module}' is registered twice.");
            }

            // SQL Server compares schema names by the database collation, case-insensitively by default.
            if (string.Equals(existing.Schema, database.Schema, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModuleDatabaseException(
                    database.Module, $"The database of module '{database.Module}' declares the schema '{database.Schema}', which module '{existing.Module}' already uses.");
            }
        }

        database.AddServices(services);
        return services;
    }
}

/// <summary>
/// A module database was declared, registered, provisioned or verified wrongly, or stands against the build in a way a host
/// refuses. A deterministic outcome: retrying does not change it. <see cref="Module"/> names the module.
/// </summary>
public sealed class ModuleDatabaseException : InvalidOperationException
{
    public ModuleDatabaseException(string module, string message)
        : base(message)
    {
        Module = module;
    }

    public ModuleDatabaseException(string module, string message, Exception innerException)
        : base(message, innerException)
    {
        Module = module;
    }

    public ModuleDatabaseException(string module, string message, ModuleDatabaseStatus status)
        : base(message)
    {
        Module = module;
        Status = status;
    }

    /// <summary>The module the database belongs to.</summary>
    public string Module { get; }

    /// <summary>The status that was refused, when the refusal is about how the database stands against the build.</summary>
    public ModuleDatabaseStatus? Status { get; }
}
