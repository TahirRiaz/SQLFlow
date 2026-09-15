namespace SqlFlow.Cli.Hosting;

/// <summary>
/// A verb a module adds to the CLI: <c>sqlflow &lt;name&gt; ...</c>. The handler runs with the parsed command line, the
/// command's service provider and, when it needs one, a client for the control plane; its return value is the process exit
/// code (0 success, 1 error, 2 a completed check that found problems, by SQLFlow's convention).
/// </summary>
public sealed class CliVerb
{
    /// <param name="name">The verb: lowercase letters, digits and hyphens, starting with a letter, at most 32 characters.</param>
    /// <param name="usage">
    /// The verb's lines in <c>sqlflow --help</c>, one per element, without leading indentation (the CLI indents them), for
    /// example <c>sqlflow check &lt;flow.yaml&gt;     Check a flow's documents</c>. At least one.
    /// </param>
    /// <param name="handler">Runs the verb and returns the exit code.</param>
    public CliVerb(string name, IReadOnlyList<string> usage, Func<CliVerbContext, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(handler);
        Name = name;
        Usage = [.. usage];
        Handler = handler;
    }

    /// <summary>The verb's name.</summary>
    public string Name { get; }

    /// <summary>The verb's help lines.</summary>
    public IReadOnlyList<string> Usage { get; }

    /// <summary>Runs the verb.</summary>
    public Func<CliVerbContext, Task<int>> Handler { get; }

    /// <summary>The verb's subcommands, offered by shell completion after the verb. Same naming rule as the verb.</summary>
    public IReadOnlyList<string> Subcommands { get; init; } = [];

    /// <summary>
    /// The verb's own options that take the next token as a value (<c>--kind</c>, <c>--release</c>). SQLFlow's value-taking
    /// options need not be repeated. Each is also offered by shell completion.
    /// </summary>
    public IReadOnlyList<string> ValueOptions { get; init; } = [];

    /// <summary>The verb's own plain flags (<c>--connect</c>), offered by shell completion.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
}
