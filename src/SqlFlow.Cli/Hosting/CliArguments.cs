using System.Collections.Frozen;
using System.Globalization;

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
        "--db", "--repo", "--repo-url", "--module",
        // The control-plane verbs (health/login/logout/trigger/runs/groups and the estate family).
        "--url", "--token", "--username", "--token-name", "--expires-days", "--scopes",
        "--scope", "--batch", "--pool", "--poll-seconds", "--commit", "--flow", "--status", "--kind", "--group",
        "--page", "--page-size", "--from", "--to", "--file-pattern", "--source-filter",
        "--cron", "--interval", "--timezone", "--max-concurrency",
        "--remote-url", "--local-path", "--credential-ref", "--credential-user",
        "--ref", "--sample", "--max-columns", "--max-candidates", "--active", "--enabled",
        "--search", "--relation", "--tier", "--server", "--operation", "--last", "--set", "--payload",
        "--branch", "--default-type", "--drain-seconds", "--node-name",
        // The powerbi verbs.
        "--subscriber", "--report-file",
        // The ai-context verbs.
        "--version",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Every SQLFlow option that is a plain flag, taking no value. Together with <see cref="BuiltInValueOptions"/> this is
    /// the whole vocabulary of SQLFlow's own verbs, which is what lets the CLI refuse an option nobody reads rather than
    /// take it for a flag and read its value as a positional argument (<see cref="UnknownOptions(IReadOnlySet{string})"/>). A verb that starts
    /// reading a new flag adds it here; <c>CliOptionInventoryTests</c> fails when one is missing.
    /// </summary>
    internal static readonly FrozenSet<string> BuiltInFlags = new[]
    {
        "--assertions", "--assertions-only", "--catchup", "--columns", "--connect",
        "--create", "--data", "--definition", "--definitions", "--detect-keys",
        "--device", "--disabled", "--down", "--dry-run", "--dump-facts",
        "--fail-on-anomaly", "--files", "--flow-columns", "--flows", "--follow", "--full",
        "--health-check", "--help", "--include-all", "--include-system", "--json", "--latest",
        "--metrics", "--no-db-sync", "--no-expiry", "--no-metadata", "--no-observed", "--no-push",
        "--no-store", "--no-tables", "--no-validate", "--no-verify", "--no-views", "--no-wait",
        "--objects", "--preview", "--provenance", "--recursive", "--remote", "--retrain", "--show-sql",
        "--statements", "--strict", "--system", "--up", "--values", "--verbose",
        "--with-token", "--yaml",

        // The short forms, which take no value either.
        "-h", "-r", "-v",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every option SQLFlow's own verbs read, value-taking and flag alike.</summary>
    public static IReadOnlySet<string> SqlFlowOptions { get; } =
        BuiltInValueOptions.Concat(BuiltInFlags).ToFrozenSet(StringComparer.Ordinal);

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

    /// <summary>
    /// The options in <paramref name="args"/> that <paramref name="known"/> does not name, in the order they were given
    /// and without repeats. A token is an option when it starts with <c>-</c> and is not a lone <c>-</c>, a negative
    /// number, or the value of a value-taking option; everything else is a positional argument.
    /// <para>
    /// This is what a verb refuses on. An option no verb reads is a mistake worth stopping for: taken for a flag, its
    /// value becomes a positional argument, and a command that asked for something specific quietly does something
    /// broader instead.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> UnknownOptions(IReadOnlyList<string> args, IReadOnlySet<string> known, IReadOnlySet<string> valueOptions)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(valueOptions);
        var unknown = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!IsOption(token))
            {
                continue;
            }

            if (valueOptions.Contains(token))
            {
                // Its value is the next token, whatever that looks like, so it is never read as an option of its own.
                i++;
                continue;
            }

            if (!known.Contains(token) && !unknown.Contains(token, StringComparer.Ordinal))
            {
                unknown.Add(token);
            }
        }

        return unknown;
    }

    /// <summary>The options in this parse that <paramref name="known"/> does not name.</summary>
    public IReadOnlyList<string> UnknownOptions(IReadOnlySet<string> known) => UnknownOptions(_args, known, _valueOptions);

    /// <summary>
    /// What to say about an option nobody reads: the option, the nearest known one when there is an obvious near miss
    /// (a hyphen, a plural or a single letter away), and where to look otherwise.
    /// </summary>
    public static string DescribeUnknown(string option, string verb, IReadOnlySet<string> known)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        ArgumentNullException.ThrowIfNull(known);
        var nearest = known
            .Select(candidate => (Option: candidate, Distance: Distance(option, candidate)))
            .Where(candidate => candidate.Distance <= 2)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Option, StringComparer.Ordinal)
            .Select(candidate => candidate.Option)
            .FirstOrDefault();
        var where = string.IsNullOrWhiteSpace(verb) ? "sqlflow --help" : $"sqlflow {verb} --help";
        return nearest is null
            ? $"'{option}' is not an option this command reads. {where} lists the ones it does."
            : $"'{option}' is not an option this command reads. Did you mean '{nearest}'? {where} lists them all.";
    }

    /// <summary>True when a token is an option rather than a positional argument or a negative number.</summary>
    private static bool IsOption(string token)
        => token.Length > 1 && token[0] == '-' && !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>The edit distance between two options, capped: only near misses are worth suggesting.</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

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
