using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog.Modules;

namespace SqlFlow.Cli.Hosting;

/// <summary>
/// A module a host composes into the <c>sqlflow</c> CLI. The CLI knows nothing about a module beyond this contract: the module
/// contributes verbs (with their usage lines and shell completions) and registers services into the service provider the
/// CLI builds for every command and for the <c>worker</c> node, so SQLFlow's own verbs (validate, run, worker) see the
/// module's flow kinds too. Modules are passed to <see cref="CliHost"/>, never discovered.
/// </summary>
public interface ICliModule
{
    /// <summary>
    /// The module's name: lowercase letters, digits and hyphens, starting with a letter, at most 64 characters. It names the
    /// module in every error the CLI prints about it, and two modules of one host may not share it.
    /// </summary>
    string Name { get; }

    /// <summary>The verbs the module adds. None may share a name with a SQLFlow verb or another module's verb.</summary>
    IReadOnlyList<CliVerb> Verbs { get; }

    /// <summary>
    /// Registers the module's services. Called after SQLFlow's own registrations (so a module may extend or replace them),
    /// once for the provider every command runs with and once for the <c>worker</c> node's provider; <see cref="CliModuleServices.Scope"/>
    /// says which.
    /// </summary>
    void ConfigureServices(CliModuleServices services);
}

/// <summary>Which service provider a module is registering into.</summary>
public enum CliServiceScope
{
    /// <summary>The provider a CLI command runs with (SQLFlow's verbs and the modules' verbs).</summary>
    Command,

    /// <summary>The provider of a <c>sqlflow worker</c> compute node, which executes runs handed out by the control plane.</summary>
    Worker,
}

/// <summary>What a module sees while the CLI builds a service provider. Handed to <see cref="ICliModule.ConfigureServices"/>.</summary>
public sealed class CliModuleServices
{
    internal CliModuleServices(string moduleName, IServiceCollection services, CliArguments arguments, CliServiceScope scope)
    {
        ModuleName = moduleName;
        Services = services;
        Arguments = arguments;
        Scope = scope;
    }

    /// <summary>The name of the module being configured.</summary>
    public string ModuleName { get; }

    /// <summary>The service collection, holding SQLFlow's registrations already.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The command line the provider is built for (for options such as <c>--db</c> that decide a registration).</summary>
    public CliArguments Arguments { get; }

    /// <summary>Whether this is a command's provider or a worker node's.</summary>
    public CliServiceScope Scope { get; }

    /// <summary>
    /// Registers the module's own database: <c>sqlflow db migrate</c> and <c>db status</c> cover it after the catalog, a worker
    /// node verifies it before taking work, and its context is available as <c>IDbContextFactory&lt;TContext&gt;</c>. On a
    /// command the catalog connection is <c>--db</c>, else <c>${env:SQLFLOW_CATALOG_DB}</c>; a worker node has none, so a
    /// module database a node opens needs a connection reference of its own.
    /// </summary>
    /// <exception cref="CliModuleException">The database belongs to another module name, or its name or schema is taken.</exception>
    public void AddDatabase(ModuleDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!string.Equals(database.Module, ModuleName, StringComparison.Ordinal))
        {
            throw new CliModuleException(
                ModuleName, $"CLI module '{ModuleName}' registers the database of module '{database.Module}'; a module registers its own database, under its own name.");
        }

        try
        {
            Services.AddModuleDatabase(database);
        }
        catch (ModuleDatabaseException ex)
        {
            throw new CliModuleException(ModuleName, ex.Message, ex);
        }
    }
}

/// <summary>A module could not be registered or configured. <see cref="ModuleName"/> names it.</summary>
public sealed class CliModuleException : InvalidOperationException
{
    public CliModuleException(string moduleName, string message)
        : base(message)
    {
        ModuleName = moduleName;
    }

    public CliModuleException(string moduleName, string message, Exception innerException)
        : base(message, innerException)
    {
        ModuleName = moduleName;
    }

    /// <summary>The offending module's name, as it declared it.</summary>
    public string ModuleName { get; }
}
