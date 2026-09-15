using SqlFlow.Cli.Remote;

namespace SqlFlow.Cli.Hosting;

/// <summary>What a module verb's handler runs with. Handed to <see cref="CliVerb.Handler"/>.</summary>
public sealed class CliVerbContext
{
    private readonly CliVerb _verb;

    internal CliVerbContext(string moduleName, CliVerb verb, CliArguments arguments, IServiceProvider services, bool verbose, CancellationToken cancellationToken)
    {
        ModuleName = moduleName;
        _verb = verb;
        Arguments = arguments;
        Services = services;
        Verbose = verbose;
        CancellationToken = cancellationToken;
        Out = Console.Out;
        Error = Console.Error;
    }

    /// <summary>The name of the module the verb belongs to.</summary>
    public string ModuleName { get; }

    /// <summary>The verb being run.</summary>
    public string Verb => _verb.Name;

    /// <summary>The command line, parsed with SQLFlow's value-taking options and the verb's own.</summary>
    public CliArguments Arguments { get; }

    /// <summary>The command's service provider: SQLFlow's engine composition plus every module's registrations.</summary>
    public IServiceProvider Services { get; }

    /// <summary>True when <c>--json</c> was given: stdout must then carry exactly one JSON document, and notes go to stderr.</summary>
    public bool Json => Arguments.HasFlag("--json");

    /// <summary>True when <c>-v</c> or <c>--verbose</c> was given.</summary>
    public bool Verbose { get; }

    /// <summary>Cancelled on the first Ctrl+C; a second one ends the process. A handler cancelled through it exits with 130.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Where the verb's own output goes: the console's standard output when the verb started.</summary>
    public TextWriter Out { get; }

    /// <summary>Where notes, warnings and errors go: the console's standard error when the verb started.</summary>
    public TextWriter Error { get; }

    /// <summary>
    /// A client for the control plane the command line points at, resolved as SQLFlow's remote verbs resolve it: the URL from
    /// <c>--url</c> then <c>SQLFLOW_URL</c>, the credential from <c>--token</c>, then <c>SQLFLOW_TOKEN</c>, then the one
    /// <c>sqlflow login</c> stored for the URL. The caller disposes it.
    /// </summary>
    /// <exception cref="Core.SqlFlowException">No control plane is configured, or its URL is not an absolute http(s) URL.</exception>
    public CliControlPlaneClient ConnectControlPlane()
    {
        var args = Arguments.All as string[] ?? [.. Arguments.All];
        var url = RemoteVerbs.RequireUrl(args);
        return new CliControlPlaneClient(RemoteVerbs.CreateAuthenticatedClient(url, args), url);
    }

    /// <summary>Prints <paramref name="reason"/> (when given) and the verb's usage lines to stderr, and returns exit code 1.</summary>
    public int UsageError(string? reason = null)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Error.WriteLine($"ERROR  {reason}");
        }

        Error.WriteLine("Usage:");
        foreach (var line in _verb.Usage)
        {
            Error.WriteLine($"  {line}");
        }

        return 1;
    }
}
