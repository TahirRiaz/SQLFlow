using System.Collections.Frozen;

namespace SqlFlow.Cli.Hosting;

/// <summary>
/// A command line as the <c>sqlflow</c> CLI reads it: positional arguments (the verb, its subcommand, a file) with every
/// option allowed anywhere. An option either takes the next token as its value (<c>--db ${env:X}</c>) or is a plain flag
/// (<c>--json</c>); the parser must know which, or a value would be mistaken for a positional. SQLFlow's own value-taking
/// options are always known; a module verb declares its own through <see cref="CliVerb.ValueOptions"/>.
/// </summary>
public sealed class CliArguments
{
    /// <summary>Every SQLFlow option that consumes the next token as its value. Kept in sync with the option readers of
    /// SQLFlow's own verbs so an option's value is never mistaken for a positional argument (the command and the file),
    /// regardless of where the user places the option.</summary>
    internal static readonly FrozenSet<string> BuiltInValueOptions = new[]
    {
        "-o", "--out", "--log-level",
        "--max-files", "--max-records", "--max-depth",
        "--source", "--target", "--database", "--schema", "--target-schema", "--provider",
        "--like", "--offset", "--limit", "--term", "--object", "--target-object", "--keys", "--name",
        "--pattern", "--root", "--keep", "--include", "--exclude", "--explode", "--aliases",
        "--separator", "--join-separator", "--map", "--array", "--repeat", "--xml",
        "--date-column", "--base-value", "--filter", "--threshold", "--alpha", "--budget", "--maturity", "--state-dir",
        "--of", "--explain",
        "--db", "--repo", "--repo-url",
        // The control-plane verbs (health/login/logout/trigger/runs/groups and the estate family).
        "--url", "--token", "--username", "--token-name", "--expires-days", "--scopes",
        "--scope", "--batch", "--pool", "--poll-seconds", "--commit", "--flow", "--status", "--kind", "--group",
        "--page", "--page-size", "--from", "--to", "--file-pattern", "--source-filter",
        "--cron", "--interval", "--timezone", "--max-concurrency",
        "--remote-url", "--credential-ref", "--credential-user",
        "--ref", "--sample", "--max-columns", "--max-candidates", "--active", "--enabled",
        "--search", "--relation", "--tier", "--server", "--operation", "--last", "--set", "--payload",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly string[] _args;
    private readonly FrozenSet<string> _valueOptions;

    /// <summary>Parses <paramref name="args"/>, treating SQLFlow's value-taking options and
    /// <paramref name="additionalValueOptions"/> as options that consume the next token.</summary>
    public CliArguments(IEnumerable<string> args, IEnumerable<string>? additionalValueOptions = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        _args = [.. args];
        if (Array.Exists(_args, a => a is null))
        {
            throw new ArgumentException("A command-line argument is null.", nameof(args));
        }

        var valueOptions = new HashSet<string>(BuiltInValueOptions, StringComparer.Ordinal);
        foreach (var option in additionalValueOptions ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(option, nameof(additionalValueOptions));
            valueOptions.Add(option);
        }

        _valueOptions = valueOptions.ToFrozenSet(StringComparer.Ordinal);
        Positionals = ParsePositionals(_args, _valueOptions);
    }

    /// <summary>SQLFlow's own value-taking options.</summary>
    public static IReadOnlySet<string> SqlFlowValueOptions => BuiltInValueOptions;

    /// <summary>Every argument, in order, as given.</summary>
    public IReadOnlyList<string> All => _args;

    /// <summary>The positional arguments in order: the verb first, then its subcommand or file, and so on.</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>The positional argument at <paramref name="index"/>, or null when there are not that many.</summary>
    public string? Positional(int index) => index >= 0 && index < Positionals.Count ? Positionals[index] : null;

    /// <summary>True when <paramref name="option"/> takes the next token as its value in this parse.</summary>
    public bool IsValueOption(string option) => _valueOptions.Contains(option);

    /// <summary>
    /// The value after the first of <paramref name="names"/> present, or null when none is present or the option has no
    /// value. A token that looks like another option (<c>--x</c>) is not taken as a value, so a value-taking option given
    /// without one never swallows the next flag; a lone <c>-</c> is a value.
    /// </summary>
    public string? GetOption(params string[] names) => FindOption(_args, names);

    /// <summary>Every value of a repeatable option (<c>--set a=1 --set b=2</c>), in order, read as <see cref="GetOption"/> reads one.</summary>
    public IReadOnlyList<string> GetOptions(string name) => FindOptions(_args, name);

    /// <summary>True when any of <paramref name="names"/> is present as an argument.</summary>
    public bool HasFlag(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return Array.Exists(_args, a => Array.IndexOf(names, a) >= 0);
    }

    /// <summary>The value of the first of <paramref name="names"/> present as a non-negative integer, or
    /// <paramref name="fallback"/> when it is absent, not an integer, or negative.</summary>
    public int GetNonNegativeIntOption(int fallback, params string[] names) => ParseNonNegativeInt(GetOption(names), fallback);

    internal static string[] ParsePositionals(IReadOnlyList<string> args, IReadOnlySet<string> valueOptions)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (valueOptions.Contains(args[i]))
                {
                    i++;
                }

                continue;
            }

            positional.Add(args[i]);
        }

        return [.. positional];
    }

    internal static string? FindOption(IReadOnlyList<string> args, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (names.Contains(args[i]))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index + 1 >= args.Count)
        {
            return null;
        }

        // A value-taking flag with no value would otherwise swallow the next flag (e.g. `--explode --data`
        // setting explodePaths to "--data"). A lone "-" is still allowed (e.g. a separator).
        var value = args[index + 1];
        return value.Length > 1 && value[0] == '-' ? null : value;
    }

    internal static IReadOnlyList<string> FindOptions(IReadOnlyList<string> args, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name && !(args[i + 1].Length > 1 && args[i + 1][0] == '-'))
            {
                values.Add(args[i + 1]);
                i++;
            }
        }

        return values;
    }

    internal static int ParseNonNegativeInt(string? value, int fallback)
        => value is not null && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : fallback;
}
