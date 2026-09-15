using SqlFlow.Core.Hosting;

namespace SqlFlow.ControlPlane.Hosting;

/// <summary>Registers control plane modules. <see cref="ControlPlaneHost"/> uses it for the modules a host passes.</summary>
public static class ControlPlaneModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds <paramref name="module"/> to a control plane's service collection: validates its name, runs its
    /// <see cref="IControlPlaneModule.ConfigureServices"/> against <paramref name="services"/>, and records it so the host maps
    /// its endpoints once it is built. Call it after SQLFlow's own registrations and before the host is built.
    /// </summary>
    /// <exception cref="ControlPlaneModuleException">
    /// The name is invalid, another module already has it, or the module's configuration failed.
    /// </exception>
    public static IServiceCollection AddControlPlaneModule(
        this IServiceCollection services, IControlPlaneModule module, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var name = ValidName(module);
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is ControlPlaneModuleRegistration registered
                && string.Equals(registered.Module.Name, name, StringComparison.Ordinal))
            {
                throw new ControlPlaneModuleException(name, $"Control plane module '{name}' is registered twice; module names must be unique within a host.");
            }
        }

        var limits = ControlPlaneLimits.GetOrAdd(services);
        services.AddSingleton(new ControlPlaneModuleRegistration(module));
        try
        {
            module.ConfigureServices(new ControlPlaneModuleServices(name, services, configuration, environment, limits));
        }
        catch (Exception ex) when (ex is not ControlPlaneModuleException)
        {
            throw new ControlPlaneModuleException(name, $"Control plane module '{name}' failed to configure its services: {ex.Message}", ex);
        }

        return services;
    }

    /// <summary>Maps the endpoints of every module registered in the built application, in registration order.</summary>
    internal static void MapModuleEndpoints(IServiceProvider services, RouteGroupBuilder api)
    {
        foreach (var registration in services.GetServices<ControlPlaneModuleRegistration>())
        {
            var name = registration.Module.Name;
            try
            {
                registration.Module.MapEndpoints(new ControlPlaneModuleEndpoints(name, services, api));
            }
            catch (Exception ex) when (ex is not ControlPlaneModuleException)
            {
                throw new ControlPlaneModuleException(name, $"Control plane module '{name}' failed to map its endpoints: {ex.Message}", ex);
            }
        }
    }

    private static string ValidName(IControlPlaneModule module)
    {
        var name = module.Name;
        if (string.IsNullOrEmpty(name))
        {
            throw new ControlPlaneModuleException(
                name ?? string.Empty, $"The control plane module of type '{module.GetType().FullName}' has no name.");
        }

        if (!HostModuleNames.IsValid(name))
        {
            throw new ControlPlaneModuleException(
                name,
                $"'{name}' (module type '{module.GetType().FullName}') is not a valid control plane module name: use {HostModuleNames.Rule}.");
        }

        return name;
    }
}

/// <summary>A module registered with a host, recorded in the service collection so the host maps its endpoints.</summary>
internal sealed record ControlPlaneModuleRegistration(IControlPlaneModule Module);

/// <summary>A control plane module could not be registered, configured or mapped. <see cref="ModuleName"/> names it.</summary>
public sealed class ControlPlaneModuleException : InvalidOperationException
{
    public ControlPlaneModuleException(string moduleName, string message)
        : base(message)
    {
        ModuleName = moduleName;
    }

    public ControlPlaneModuleException(string moduleName, string message, Exception innerException)
        : base(message, innerException)
    {
        ModuleName = moduleName;
    }

    /// <summary>The offending module's name, as it declared it.</summary>
    public string ModuleName { get; }
}
