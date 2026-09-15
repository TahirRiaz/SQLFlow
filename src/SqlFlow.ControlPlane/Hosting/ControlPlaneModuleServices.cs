using Microsoft.Extensions.Options;
using SqlFlow.Catalog.Modules;

namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// What a module sees while the control plane is being built: the service collection, the host's configuration and
/// environment, and the limits the platform owns. Handed to <see cref="IControlPlaneModule.ConfigureServices"/>.
/// </summary>
public sealed class ControlPlaneModuleServices
{
    private readonly ControlPlaneLimits _limits;

    internal ControlPlaneModuleServices(
        string moduleName, IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, ControlPlaneLimits limits)
    {
        ModuleName = moduleName;
        Services = services;
        Configuration = configuration;
        Environment = environment;
        _limits = limits;
    }

    /// <summary>The name of the module being configured.</summary>
    public string ModuleName { get; }

    /// <summary>The control plane's service collection, holding SQLFlow's registrations already.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The host's configuration (appsettings, environment variables, command line).</summary>
    public IConfiguration Configuration { get; }

    /// <summary>The host's environment.</summary>
    public IHostEnvironment Environment { get; }

    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from the configuration section at <paramref name="sectionPath"/>, validates it
    /// now, and registers it as <see cref="IOptions{TOptions}"/> validated again on start. Returns the bound value, so a
    /// module can decide what to register from it (a background service behind an <c>Enabled</c> switch, say) exactly as
    /// the platform does with its own options. A missing section binds the type's defaults.
    /// </summary>
    /// <param name="sectionPath">The configuration path, for example <c>Module:Rollout</c>.</param>
    /// <param name="validate">
    /// Checks a bound value and throws <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/> naming the
    /// offending setting. Null when every bound value is valid.
    /// </param>
    /// <exception cref="ControlPlaneModuleException">The section does not bind to the type, or the value is invalid.</exception>
    public TOptions AddOptions<TOptions>(string sectionPath, Action<TOptions>? validate = null)
        where TOptions : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        var section = Configuration.GetSection(sectionPath);

        TOptions bound;
        try
        {
            bound = section.Get<TOptions>() ?? new TOptions();
        }
        catch (InvalidOperationException ex)
        {
            // The binder's own failure (a value that does not convert to the property's type) names the key and type.
            throw Invalid(sectionPath, ex);
        }

        if (validate is not null)
        {
            var failure = Check(bound, validate);
            if (failure is not null)
            {
                throw Invalid(sectionPath, failure);
            }
        }

        var builder = Services.AddOptions<TOptions>().Bind(section);
        if (validate is not null)
        {
            builder.Validate(
                value => Check(value, validate) is null,
                $"Control plane module '{ModuleName}': configuration section '{sectionPath}' is invalid; see the startup validation message for the specific setting.");
        }

        builder.ValidateOnStart();
        return bound;
    }

    /// <summary>
    /// Registers the module's own database. Bootstrap migrates it after the catalog (when the control plane applies
    /// migrations) and verifies it against the build, and the control plane refuses to run when it is missing, behind or
    /// ahead. The module's context is available as <c>IDbContextFactory&lt;TContext&gt;</c>.
    /// </summary>
    /// <exception cref="ControlPlaneModuleException">The database belongs to another module name, or its name or schema is taken.</exception>
    public void AddDatabase(ModuleDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!string.Equals(database.Module, ModuleName, StringComparison.Ordinal))
        {
            throw new ControlPlaneModuleException(
                ModuleName, $"Control plane module '{ModuleName}' registers the database of module '{database.Module}'; a module registers its own database, under its own name.");
        }

        try
        {
            Services.AddModuleDatabase(database);
        }
        catch (ModuleDatabaseException ex)
        {
            throw new ControlPlaneModuleException(ModuleName, ex.Message, ex);
        }
    }

    /// <summary>Registers a hosted service the control plane starts and stops with the host.</summary>
    public void AddHostedService<THostedService>()
        where THostedService : class, IHostedService
        => Services.AddHostedService<THostedService>();

    /// <summary>
    /// Raises the largest request body the control plane accepts to at least <paramref name="bytes"/>. The platform keeps
    /// the largest value any module asks for; a smaller request leaves the limit where it is.
    /// </summary>
    /// <exception cref="ControlPlaneModuleException">
    /// <paramref name="bytes"/> is not positive or exceeds <see cref="ControlPlaneLimits.MaxRequestBodySizeCeiling"/>.
    /// </exception>
    public void RaiseMaxRequestBodySize(long bytes) => _limits.RaiseMaxRequestBodySize(ModuleName, bytes);

    private static Exception? Check<TOptions>(TOptions value, Action<TOptions> validate)
    {
        try
        {
            validate(value);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return ex;
        }
    }

    private ControlPlaneModuleException Invalid(string sectionPath, Exception cause)
        => new(ModuleName, $"Control plane module '{ModuleName}': configuration section '{sectionPath}' is invalid: {cause.Message}", cause);
}
