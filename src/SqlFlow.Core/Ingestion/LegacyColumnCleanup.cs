using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Ingestion;

/// <summary>
/// The legacy SQLFlow column-name cleanup, replicated exactly for backwards compatibility: a flow run by V3
/// must produce the SAME target column names a legacy install produced, or schema-sync would treat every
/// existing column as missing and try to add a renamed duplicate.
///
/// There are two distinct legacy algorithms, faithfully reproduced here:
/// <list type="bullet">
///   <item><b>File flows</b> (CSV/XLS/JSON/XML/Parquet), legacy <c>Shared.cs</c>: ALWAYS on (no flag) for any
///   header-derived name. Each name has every invalid character replaced with an underscore via the config
///   regex, and a collision (case-insensitive) is resolved by appending the column's ORDINAL index. See
///   <see cref="CleanFileColumnNames"/>.</item>
///   <item><b>Relational sync</b>, legacy <c>SMOHelper.CleanupColumnName</c>: OPT-IN, with a configurable
///   replacement and a numeric-suffix dedup; that path lives in <see cref="DefaultColumnNameCleaner"/>, which
///   uses <see cref="DefaultCleanupRegex"/> as the config fallback.</item>
/// </list>
///
/// The default regex is the canonical legacy <c>flw.SysCFG.ColCleanupSQLRegExp</c> value: it keeps ASCII
/// letters, digits, underscore, and the Norwegian letters æøåÆØÅ, and replaces everything else. The Norwegian
/// letters are spelled with <c>\u</c> escapes so the pattern is independent of the source file encoding.
/// </summary>
public static class LegacyColumnCleanup
{
    /// <summary>The canonical legacy default cleanup regex (<c>flw.SysCFG.ColCleanupSQLRegExp</c>):
    /// <c>[^a-zA-Z0-9æøåÆØÅ_]</c>. Any character it matches is an "invalid" character. It keeps ASCII letters,
    /// digits, underscore, and the six Norwegian letters U+00E6 æ, U+00F8 ø, U+00E5 å, U+00C6 Æ, U+00D8 Ø,
    /// U+00C5 Å (this file is UTF-8); a unit test pins the exact character set so an encoding accident cannot
    /// silently change it.</summary>
    public const string DefaultCleanupRegex = "[^a-zA-Z0-9æøåÆØÅ_]";

    /// <summary>The file-flow replacement: an underscore, hardcoded in legacy <c>Shared.cs</c> (it does not
    /// use the configurable relational replacement).</summary>
    public const string FileReplacement = "_";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Compiling the default pattern once is safe: it is a constant and the cleanup runs per file schema read.
    private static readonly Regex DefaultRegex =
        new(DefaultCleanupRegex, RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// Cleans file-flow column names exactly as legacy <c>Shared.cs</c> did: every invalid character (per the
    /// regex) becomes an underscore, and a name that collides case-insensitively with an earlier one has the
    /// column's zero-based ordinal index appended. The result is index-aligned with <paramref name="rawNames"/>
    /// (the cell order is unchanged), so the positional row reader stays correct.
    /// </summary>
    /// <param name="rawNames">The header-derived names, in file order.</param>
    /// <param name="regexPattern">An override for the cleanup regex; the canonical legacy default when null.</param>
    public static IReadOnlyList<string> CleanFileColumnNames(IReadOnlyList<string> rawNames, string? regexPattern = null)
    {
        ArgumentNullException.ThrowIfNull(rawNames);

        var regex = regexPattern is null or "" ? DefaultRegex : Compile(regexPattern);

        // Case-insensitive, matching the legacy ContainsKeyIgnoreCase collision check (and SQL Server's
        // default case-insensitive column identity).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(rawNames.Count);

        for (var i = 0; i < rawNames.Count; i++)
        {
            var cleaned = regex.Replace(rawNames[i] ?? string.Empty, FileReplacement);
            var name = cleaned;
            if (!seen.Add(name))
            {
                // Legacy appends the current column's ordinal index on collision.
                name = cleaned + i.ToString(CultureInfo.InvariantCulture);

                // Legacy would throw if even the ordinal-suffixed name collided (a degenerate case that could
                // never have produced a valid legacy table); keep disambiguating instead of throwing, which
                // only changes behavior where legacy already failed.
                var extra = i;
                while (!seen.Add(name))
                {
                    extra++;
                    name = cleaned + "_" + extra.ToString(CultureInfo.InvariantCulture);
                }
            }

            result.Add(name);
        }

        return result;
    }

    private static Regex Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Invalid column-cleanup regex '{pattern}': {ex.Message}", ex);
        }
    }
}
