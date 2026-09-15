using SqlFlow.Core.Hosting;

namespace SqlFlow.Cli.Hosting;

/// <summary>
/// The <c>sqlflow</c> CLI's entry point for a host: SQLFlow's own program runs it with no modules; a product built on SQLFlow
/// has a program of its own that runs it with its modules, and gets every SQLFlow verb plus the modules' verbs, help lines and
/// shell completions.
/// </summary>
public static class CliHost
{
    /// <summary>Runs one command line, branded as SQLFlow, and returns the process exit code.</summary>
    /// <exception cref="CliModuleException">A module or one of its verbs breaks a rule (thrown before anything runs).</exception>
    public static Task<int> RunAsync(string[] args, params ICliModule[] modules)
        => RunAsync(args, ProductBranding.SqlFlow, modules);

    /// <summary>
    /// Runs one command line, branded as <paramref name="branding"/> (the banner of the help reads
    /// <c>sqlflow - </c> and the branding's CLI tagline), and returns the process exit code. The branding is registered in
    /// every service provider the CLI builds, for module verbs to use.
    /// </summary>
    /// <exception cref="CliModuleException">A module or one of its verbs breaks a rule (thrown before anything runs).</exception>
    public static Task<int> RunAsync(string[] args, ProductBranding branding, params ICliModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(modules);
        return Program.RunAsync(args, new CliModuleSet(modules, branding));
    }
}
