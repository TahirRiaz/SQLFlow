namespace SqlFlow.Node;

/// <summary>
/// The name a compute node polls under: the identity the dispatcher leases work to, the fleet registry keys its row on,
/// and the rate limiter partitions on. By default it is the machine name, which an orchestrator makes unique per
/// replica. A launcher that starts its own workers names each one instead (through <c>--node-name</c> or
/// <see cref="EnvironmentVariable"/>), so it can recognise the worker it started when that worker first polls.
/// </summary>
public sealed record NodeIdentity
{
    /// <summary>The environment variable a node reads its name from when <c>--node-name</c> is not given.</summary>
    public const string EnvironmentVariable = "SQLFLOW_NODE_NAME";

    /// <summary>The longest name a node may carry: well inside the fleet registry's key column and every header and log
    /// line the name travels in.</summary>
    public const int MaxLength = 128;

    private NodeIdentity(string name) => Name = name;

    /// <summary>The node's name.</summary>
    public string Name { get; }

    /// <summary>This machine's name, the identity of a node nobody named.</summary>
    public static NodeIdentity Machine() => new(Environment.MachineName);

    /// <summary>The node named <paramref name="configured"/>, or this machine's name when it is null or blank. A
    /// configured name is 1 to <see cref="MaxLength"/> characters of ASCII letters, digits, '.', '-' and '_', starting
    /// with a letter or digit; anything else is refused with the reason, because a name the registry, the header or a
    /// launcher would read differently could hand one worker's leases to another.</summary>
    /// <exception cref="ArgumentException">The configured name is not a valid node name.</exception>
    public static NodeIdentity Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Machine();
        }

        var name = configured.Trim();
        if (name.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A node name is at most {MaxLength} characters; '{name[..32]}...' has {name.Length}.", nameof(configured));
        }

        if (!char.IsAsciiLetterOrDigit(name[0]))
        {
            throw new ArgumentException(
                $"A node name starts with a letter or digit; '{name}' does not.", nameof(configured));
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                throw new ArgumentException(
                    $"A node name holds only ASCII letters, digits, '.', '-' and '_'; '{name}' holds '{c}'.", nameof(configured));
            }
        }

        return new NodeIdentity(name);
    }
}
