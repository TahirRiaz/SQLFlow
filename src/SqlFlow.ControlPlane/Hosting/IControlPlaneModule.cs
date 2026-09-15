namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// A module a host composes into the control plane. The control plane knows nothing about a module beyond this
/// contract: the module registers its services, options and hosted services while the host is being built, and maps its
/// endpoints onto the authenticated route groups once it is built. A module is registered through
/// <see cref="ControlPlaneHost"/> (or <see cref="ControlPlaneModuleServiceCollectionExtensions.AddControlPlaneModule"/>),
/// never discovered.
/// </summary>
/// <remarks>
/// Both calls happen exactly once per host, <see cref="ConfigureServices"/> after SQLFlow's own registrations (so a module
/// may extend or replace them) and <see cref="MapEndpoints"/> after SQLFlow's own endpoints. An exception from either is
/// rethrown as a <see cref="ControlPlaneModuleException"/> naming the module, and the host does not start.
/// </remarks>
public interface IControlPlaneModule
{
    /// <summary>
    /// The module's name: lowercase letters, digits and hyphens, starting with a letter, at most 64 characters. It names
    /// the module in every error and log line the platform writes about it, and two modules of one host may not share it.
    /// </summary>
    string Name { get; }

    /// <summary>Registers the module's services, options and hosted services, and raises the platform limits it needs.</summary>
    void ConfigureServices(ControlPlaneModuleServices services);

    /// <summary>Maps the module's endpoints onto the control plane's authenticated route groups.</summary>
    void MapEndpoints(ControlPlaneModuleEndpoints endpoints);
}
