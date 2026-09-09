using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;

namespace SqlFlow.Core.Ingestion;

/// <summary>
/// The default column-name cleaner: a "remove invalid characters" regex (from the policy) is applied to each
/// name, replacing matches with <see cref="SchemaSyncPolicy.ReplaceInvalidCharsWith"/> (or removing them when
/// blank); an empty result becomes a placeholder; collisions are de-duplicated with a numeric suffix.
/// </summary>
public sealed class DefaultColumnNameCleaner : IColumnNameCleaner
{
    private const string EmptyColumnPlaceholder = "EmptyColumnName";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public IReadOnlyList<string> Clean(IReadOnlyList<string> rawNames, SchemaSyncPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(rawNames);
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.CleanColumnNames)
        {
            return rawNames;
        }

        // Legacy fell back to the flw.SysCFG.ColCleanupSQLRegExp config default when the per-flow regex was
        // blank; the canonical default reproduces that, so enabling cleanup without an explicit regex behaves
        // exactly as a legacy install with the shipped default did.
        var pattern = string.IsNullOrEmpty(policy.CleanColumnNameRegex)
            ? LegacyColumnCleanup.DefaultCleanupRegex
            : policy.CleanColumnNameRegex;

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Invalid CleanColumnNameRegex '{pattern}': {ex.Message}", ex);
        }

        var replacement = policy.ReplaceInvalidCharsWith ?? string.Empty;
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(rawNames.Count);

        foreach (var raw in rawNames)
        {
            var cleaned = regex.Replace(raw ?? string.Empty, replacement);
            if (cleaned.Length == 0)
            {
                cleaned = EmptyColumnPlaceholder;
            }

            result.Add(Deduplicate(cleaned, used));
        }

        return result;
    }

    private static string Deduplicate(string name, Dictionary<string, int> used)
    {
        if (!used.TryGetValue(name, out var counter))
        {
            used[name] = 1;
            return name;
        }

        string candidate;
        do
        {
            candidate = name + counter.ToString(CultureInfo.InvariantCulture);
            counter++;
        }
        while (used.ContainsKey(candidate));

        used[name] = counter;
        used[candidate] = 1;
        return candidate;
    }
}
