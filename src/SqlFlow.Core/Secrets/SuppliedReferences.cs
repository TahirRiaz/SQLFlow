using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Secrets;

/// <summary>
/// The values the control plane supplies with a run for the <c>${env:NAME}</c> references its flows use, and the scope
/// in which a run resolves with them. Inside a scope opened by <see cref="Supply"/>, every <see cref="SecretResolver"/>
/// answers <c>${env:NAME}</c> from the supplied values first and falls back to the process environment only for a name
/// they do not hold, so a node needs no environment of its own for what its runs read: the control plane hands each run
/// the connections and settings its repository resolves to, and the node resolves them with its own identity.
/// </summary>
/// <remarks>
/// <para>
/// A supplied value may itself be a reference (<c>${keyvault:vault/secret}</c>, <c>${env:OTHER}</c>), so the control
/// plane can point every run at a secret without ever holding it: the value is substituted and then resolved by the
/// process's own providers. Values do not chain: a supplied <c>${env:OTHER}</c> is read from the process environment even
/// when <c>OTHER</c> is supplied too, so no two values naming each other can loop. A value that is exactly its own
/// reference (<c>NAME = ${env:NAME}</c>) hands the name back to the process environment: that is how a narrower scope
/// says its nodes' own environment decides.
/// </para>
/// <para>
/// Scopes nest: a scope opened inside another resolves with its own values over the outer scope's, so a module can lay a
/// narrower set (say, one tenant's) over the values every run carries. The scope flows with the asynchronous call
/// that opened it, so concurrent runs in one process each resolve with their own values.
/// </para>
/// <para>
/// A run can say where each reference it resolves inside a scope takes its value (<see cref="ReportTo"/>): by name and
/// origin, never by value, once per name and at most <see cref="MaxReported"/> names, so what a run's trace says about its
/// references is bounded by what its documents declare and never by how much data it moves. The report belongs to the run,
/// not to one scope, so a narrower scope the run opens later reports through it too.
/// </para>
/// </remarks>
public static partial class SuppliedReferences
{
    /// <summary>The most values one run carries.</summary>
    public const int MaxCount = 64;

    /// <summary>The longest name: an environment variable name, which is what a flow spells it as.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest value. A value is an identifier, a URL, a connection string or a reference, never a document.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The most names one scope reports where they take their value; a scope resolving more says so once.</summary>
    public const int MaxReported = 100;

    private static readonly IReadOnlyDictionary<string, string> Empty = ReadOnlyDictionary<string, string>.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The secret-bearing names, by their ending: a value under such a name is a credential, so it must be a whole
    /// reference and never the credential itself.
    /// </summary>
    private static readonly string[] SecretNameEndings = ["SECRET", "PASSWORD", "TOKEN", "APIKEY", "API_KEY"];

    private static readonly AsyncLocal<Scope?> Ambient = new();

    private static readonly AsyncLocal<Reporter?> AmbientReporter = new();

    /// <summary>The values the current scope resolves with (its own over every enclosing scope's); empty outside a scope.</summary>
    public static IReadOnlyDictionary<string, string> Current => Ambient.Value?.Values ?? Empty;

    /// <summary>
    /// Opens a scope resolving with <paramref name="values"/> over the current scope's, until the returned handle is
    /// disposed. The values are validated first (<see cref="Validate"/>). A scope opened with no values answers nothing
    /// and resolves every reference as the enclosing scope or the process environment does, but it still reports where
    /// each reference takes its value (<see cref="ReportTo"/>): that is how a run given nothing says its values came from
    /// the node. A caller that wants a run given nothing to behave and report exactly as before there was anything to
    /// give opens no scope at all.
    /// </summary>
    /// <exception cref="SqlFlowException">The values break a rule <see cref="Validate"/> names.</exception>
    public static IDisposable Supply(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Validate(values, "the supplied references");
        var outer = Ambient.Value;
        var merged = new Dictionary<string, string>(outer?.Values ?? Empty, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            merged[name] = value;
        }

        var scope = new Scope(outer, merged);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Has the run executing on this asynchronous call say, through <paramref name="report"/>, where each
    /// <c>${env:NAME}</c> reference it resolves inside a scope from now on takes its value: once per name, at most
    /// <see cref="MaxReported"/> names, through every scope the run opens, an enclosing one or a narrower one opened later.
    /// Outside a scope there is nothing supplied to tell apart from the environment, and nothing is reported. The report
    /// ends with the asynchronous call that set it, so it never outlives the run.
    /// </summary>
    public static void ReportTo(Action<SuppliedReferenceUse> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        AmbientReporter.Value = new Reporter(report);
    }

    /// <summary>
    /// The value the current scope supplies for <paramref name="name"/>, reported to the run's report as it is looked up:
    /// false when no scope is open, when the scope does not hold the name, or when it holds the name as its own reference
    /// (<see cref="DefersToNode"/>), which leaves it to the process environment.
    /// </summary>
    internal static bool TryTake(string name, [NotNullWhen(true)] out string? value)
    {
        var scope = Ambient.Value;
        if (scope is null)
        {
            value = null;
            return false;
        }

        var answered = Answers(scope.Values, name, out value);
        AmbientReporter.Value?.Report(new SuppliedReferenceUse(name, answered, Deferred: !answered && scope.Values.ContainsKey(name)));
        return answered;
    }

    /// <summary>
    /// Whether <paramref name="supplied"/> answers the reference <paramref name="name"/>, and with what: it does when it
    /// holds the name, unless what it holds is the very reference it fills (<see cref="DefersToNode"/>). The one rule every
    /// resolution and every report of where a value came from applies, so the two never disagree.
    /// </summary>
    public static bool Answers(IReadOnlyDictionary<string, string> supplied, string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(name);
        if (supplied.TryGetValue(name, out var held) && !DefersToNode(name, held))
        {
            value = held;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Whether <paramref name="value"/>, held under <paramref name="name"/>, is exactly its own reference (<c>${env:NAME}</c>).</summary>
    public static bool DefersToNode(string name, string value)
        => string.Equals(value, "${env:" + name + "}", StringComparison.Ordinal);

    /// <summary>
    /// The names of the <c>${env:NAME}</c> references in <paramref name="value"/>, in the order they appear; none for a
    /// value without one.
    /// </summary>
    public static IEnumerable<string> NamesIn(string? value)
        => string.IsNullOrEmpty(value) || !value.Contains("${env:", StringComparison.Ordinal)
            ? []
            : EnvReference().Matches(value).Select(m => m.Groups["name"].Value);

    /// <summary>Whether <paramref name="name"/> can name a supplied value: an environment variable name of at most <see cref="MaxNameLength"/> characters.</summary>
    public static bool IsName([NotNullWhen(true)] string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    /// <summary>
    /// Refuses values no run may carry, naming the first broken rule and where the values came from
    /// (<paramref name="what"/>): more than <see cref="MaxCount"/> values; a name that is not an environment variable name;
    /// an empty value, one longer than <see cref="MaxValueLength"/> or holding a control character; a credential written
    /// out (a value under a secret-bearing name that is not a whole reference, or a connection string whose password,
    /// secret or key is not a reference). A literal secret is never carried, stored or shown: it belongs in a vault, and
    /// the value names it there.
    /// </summary>
    /// <exception cref="SqlFlowException">The values break one of the rules.</exception>
    public static void Validate(IReadOnlyDictionary<string, string> values, string what)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        if (values.Count > MaxCount)
        {
            throw new SqlFlowException($"{Capitalized(what)} hold {values.Count} values, more than the {MaxCount} a run carries.");
        }

        foreach (var (name, value) in values)
        {
            if (!IsName(name))
            {
                throw new SqlFlowException(
                    $"{Capitalized(what)} name '{name}', which is not an environment variable name: a letter or underscore, then " +
                    $"letters, digits and underscores, at most {MaxNameLength} characters.");
            }

            if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength || value.Any(char.IsControl))
            {
                throw new SqlFlowException(
                    $"{Capitalized(what)} give {name} a value that is empty, longer than {MaxValueLength} characters or holds a " +
                    "control character.");
            }

            if (NamesSecret(name) && !IsWholeReference(value))
            {
                throw new SqlFlowException(
                    $"{Capitalized(what)} give {name} a value that is not a reference. {name} names a credential, so its value " +
                    "must be a whole reference such as ${keyvault:<vault>/<secret>}, never the credential itself.");
            }

            if (EmbedsLiteralSecret(value))
            {
                throw new SqlFlowException(
                    $"{Capitalized(what)} give {name} a connection string with a password, secret or key written out. Keep the " +
                    "credential in a vault and write it as a reference (Password=${keyvault:<vault>/<secret>}), or connect " +
                    "with an identity (Authentication=Active Directory Default).");
            }
        }
    }

    /// <summary>The stored form of <paramref name="values"/>: a JSON object ordered by name, or null for none.</summary>
    public static string? ToJson(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 0
            ? null
            : JsonSerializer.Serialize(new SortedDictionary<string, string>(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal), StringComparer.Ordinal), JsonOptions);
    }

    /// <summary>
    /// Parses the stored form of supplied values; null or blank is none. Stored text that is not a JSON object of strings
    /// is a corrupt row, reported as <see cref="SqlFlowException"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return parsed is null ? Empty : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The stored supplied references are not a JSON object of strings: {ex.Message}", ex);
        }
    }

    /// <summary>Whether <paramref name="name"/> names a credential, by its ending (SECRET, PASSWORD, TOKEN, APIKEY, API_KEY).</summary>
    public static bool NamesSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretNameEndings.Any(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="value"/> is one whole <c>${scheme:locator}</c> reference and nothing else.</summary>
    public static bool IsWholeReference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WholeReference().IsMatch(value.Trim());
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a connection string that writes a credential out: one of its
    /// <c>key=value</c> pairs has a secret-bearing key (password, pwd, secret, client secret, access key, shared access
    /// key) whose value is not empty and not a whole reference.
    /// </summary>
    public static bool EmbedsLiteralSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('=', StringComparison.Ordinal) || IsWholeReference(value))
        {
            return false;
        }

        foreach (var pair in value.Split(';'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = pair[..eq].Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
            var held = pair[(eq + 1)..].Trim().Trim('\'', '"');
            if (SecretKeys.Contains(key) && held.Length > 0 && !IsWholeReference(held))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "pwd", "secret", "clientsecret", "accesskey", "sharedaccesskey", "accountkey",
    };

    private static string Capitalized(string what) => char.ToUpperInvariant(what[0]) + what[1..];

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]{0,63})\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvReference();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeReference();

    /// <summary>One open scope: its values, its own over its enclosing scope's.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly Scope? _outer;
        private bool _disposed;

        public Scope(Scope? outer, IReadOnlyDictionary<string, string> values)
        {
            _outer = outer;
            Values = values;
        }

        public IReadOnlyDictionary<string, string> Values { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(Ambient.Value, this))
            {
                Ambient.Value = _outer;
            }
        }
    }

    /// <summary>A run's report of where its references take their values: each name once, at most <see cref="MaxReported"/>.</summary>
    private sealed class Reporter(Action<SuppliedReferenceUse> report)
    {
        private readonly Lock _gate = new();
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private bool _overflowed;

        public void Report(SuppliedReferenceUse use)
        {
            lock (_gate)
            {
                if (_reported.Contains(use.Name))
                {
                    return;
                }

                if (_reported.Count >= MaxReported)
                {
                    if (_overflowed)
                    {
                        return;
                    }

                    _overflowed = true;
                    use = SuppliedReferenceUse.Overflow;
                }
                else
                {
                    _reported.Add(use.Name);
                }
            }

            report(use);
        }
    }

}

/// <summary>
/// Where one <c>${env:NAME}</c> reference a scope resolved takes its value: the values the control plane supplied, or the
/// process environment. Names only: a value may be a secret and is never carried here.
/// </summary>
/// <param name="Name">The reference name, as a flow spells it in <c>${env:NAME}</c>.</param>
/// <param name="Supplied">True when the supplied values answered it; false when the process environment did.</param>
/// <param name="Deferred">True when the supplied values hold the name as its own reference (<c>NAME = ${env:NAME}</c>), which hands it to the process environment.</param>
public sealed record SuppliedReferenceUse(string Name, bool Supplied, bool Deferred = false)
{
    /// <summary>The report a scope makes once, instead of a name, when it has already named <see cref="SuppliedReferences.MaxReported"/>.</summary>
    public static SuppliedReferenceUse Overflow { get; } = new(string.Empty, Supplied: false);

    /// <summary>True for <see cref="Overflow"/>.</summary>
    public bool IsOverflow => Name.Length == 0;

    /// <summary>The line a run's trace says for this report.</summary>
    public string Describe() => this switch
    {
        { IsOverflow: true } => $"More than {SuppliedReferences.MaxReported} references were resolved; the rest are not named.",
        { Supplied: true } => $"Reference {Name} takes its value from the configuration the control plane supplied with this run.",
        { Deferred: true } => $"Reference {Name} takes its value from this node's environment: the supplied configuration hands it there.",
        _ => $"Reference {Name} takes its value from this node's environment.",
    };
}
