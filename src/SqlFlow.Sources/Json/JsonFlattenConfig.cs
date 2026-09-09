using System.Text;

namespace SqlFlow.Sources.Json;

/// <summary>How an array that is not otherwise configured becomes a single column value.</summary>
public enum JsonArrayHandling
{
    /// <summary>Keep the array as a JSON-string column (lossless). The default.</summary>
    ToJson,

    /// <summary>Flatten the first element in place (its fields become columns under the array's path).</summary>
    FirstElement,

    /// <summary>Join the array's scalar elements with a separator into one string.</summary>
    Join,

    /// <summary>Emit the element count as the column value.</summary>
    Count,

    /// <summary>Drop the array entirely (no column).</summary>
    Skip,

    /// <summary>Emit one output row per element (cross-product with other exploded arrays).</summary>
    Explode,
}

/// <summary>
/// Declarative rules for turning a nested JSON document into flat table columns, addressed by
/// JSONPath-like expressions. A faithful adaptation of the delta-forge path-based flattener: the same
/// include / exclude / json / root / separator / array-handling semantics, retargeted at SQL Server
/// (readable, case-preserving column names instead of PostgreSQL lowercasing) and at raw-string columns
/// (the existing type-inference step types them afterward, so this layer never guesses types).
///
/// Arrays follow <see cref="ArrayHandling"/> unless their path is listed in <see cref="ExplodePaths"/>
/// (or <see cref="ArrayHandling"/> is <see cref="JsonArrayHandling.Explode"/>), in which case each element
/// becomes its own output row; multiple exploded arrays cross-product.
/// </summary>
public sealed record JsonFlattenConfig
{
    /// <summary>JSONPath the records live under (e.g. <c>$.data.records</c>). <c>$</c> = the document root.</summary>
    public string RootPath { get; init; } = "$";

    /// <summary>Whitelist: when non-empty, only these paths (and the chain needed to reach them) are flattened.</summary>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];

    /// <summary>Paths whose subtree is dropped from the output (no column is produced).</summary>
    public IReadOnlyList<string> ExcludePaths { get; init; } = [];

    /// <summary>Paths kept verbatim as a single JSON-string column instead of being flattened into children.</summary>
    public IReadOnlyList<string> JsonPaths { get; init; } = [];

    /// <summary>Array paths to explode: each element becomes its own output row. Multiple paths cross-product.</summary>
    public IReadOnlyList<string> ExplodePaths { get; init; } = [];

    /// <summary>
    /// Schema-evolution aliases: a normalized source path mapped to the output column it should feed. Lets
    /// the same logical field that moved or was renamed across dataset versions (e.g. <c>$.user.name</c> in
    /// v1 and <c>$.user.firstName</c> in v2) land in one column. Built from the <c>pathAliases</c> option.
    /// </summary>
    public IReadOnlyDictionary<string, string> PathAliasColumns { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Explicit JSONPath to column-name overrides.</summary>
    public IReadOnlyDictionary<string, string> ColumnMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Maximum nesting depth flattened into columns; deeper values become JSON strings.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>Bound on how many rows one record may explode into before the read fails loudly.</summary>
    public int MaxRowsPerRecord { get; init; } = JsonPathFlattener.DefaultMaxRowsPerRecord;

    /// <summary>Separator joining nested keys into a column name (e.g. <c>address_city</c>).</summary>
    public string Separator { get; init; } = "_";

    /// <summary>How arrays not handled by an explicit rule become a column value.</summary>
    public JsonArrayHandling ArrayHandling { get; init; } = JsonArrayHandling.ToJson;

    /// <summary>Separator used when <see cref="ArrayHandling"/> is <see cref="JsonArrayHandling.Join"/>.</summary>
    public string JoinSeparator { get; init; } = ",";

    /// <summary>True if this path's subtree should be dropped from the output.</summary>
    public bool IsExcluded(string path) => MatchesAny(ExcludePaths, path);

    /// <summary>True if this path should be kept as a single JSON-string column.</summary>
    public bool IsJsonString(string path) => MatchesAny(JsonPaths, path);

    /// <summary>True if this array path should be exploded into one row per element.</summary>
    public bool ShouldExplode(string path)
        => ArrayHandling == JsonArrayHandling.Explode || MatchesAny(ExplodePaths, path);

    /// <summary>True if this path feeds an aliased (schema-evolution) output column.</summary>
    public bool IsAliased(string path)
        => PathAliasColumns.Count > 0 && PathAliasColumns.ContainsKey(NormalizeIndices(path));

    /// <summary>
    /// True if traversal should descend through, or emit, this path. JSON-string paths are implicitly
    /// included (they must be visited to be captured). With no include list, everything is included.
    /// Otherwise a path is included when it matches an include pattern, is a descendant of one, or is an
    /// ancestor on the way to one (so parents of a whitelisted leaf are still traversed).
    /// </summary>
    public bool IsIncluded(string path)
    {
        if (IsJsonString(path))
        {
            return true;
        }

        if (IncludePaths.Count == 0)
        {
            return true;
        }

        // Concrete array indices ($.a[0].b) are normalized to wildcards so they match patterns ($.a[*].b).
        var normalized = NormalizeIndices(path);
        foreach (var pattern in IncludePaths)
        {
            if (PathMatches(normalized, pattern)
                || IsAtOrUnder(normalized, pattern)
                || pattern.StartsWith(normalized + ".", StringComparison.Ordinal)
                || pattern.StartsWith(normalized + "[", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="ancestor"/> or is nested under it at a segment boundary.</summary>
    private static bool IsAtOrUnder(string path, string ancestor)
        => path.StartsWith(ancestor, StringComparison.Ordinal)
            && (path.Length == ancestor.Length || path[ancestor.Length] is '.' or '[');

    /// <summary>
    /// The SQL column name for a JSONPath: an explicit mapping if present, else the path with its root,
    /// brackets, and dots folded onto <see cref="Separator"/> and sanitized to a valid identifier. Casing
    /// is preserved (SQL Server is case-insensitive and readable mixed case is friendlier than forcing
    /// lower case the way the PostgreSQL-targeted original did).
    /// </summary>
    public string ColumnName(string path)
    {
        if (ColumnMappings.TryGetValue(path, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return Sanitize(mapped);
        }

        // A schema-evolution alias collapses several version-specific paths onto one output column.
        if (PathAliasColumns.Count > 0
            && PathAliasColumns.TryGetValue(NormalizeIndices(path), out var alias)
            && !string.IsNullOrWhiteSpace(alias))
        {
            return Sanitize(alias);
        }

        var work = path;
        if (work.StartsWith("$.", StringComparison.Ordinal))
        {
            work = work[2..];
        }
        else if (work.StartsWith('$'))
        {
            work = work[1..];
        }

        // Array subscripts ([*], [0], [12]) are removed entirely so an exploded element's column name is
        // stable no matter which element index produced it ($.details[0].track_id and $.details[7].track_id
        // both become details_track_id).
        work = RemoveSubscripts(work).Replace(".", Separator, StringComparison.Ordinal);

        return Sanitize(work);
    }

    private string Sanitize(string value)
    {
        var sep = string.IsNullOrEmpty(Separator) ? "_" : Separator;
        var sepFirst = sep[0];

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || sep.Contains(ch, StringComparison.Ordinal))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(sepFirst);
            }
        }

        var result = sb.ToString();

        // Collapse runs of the separator into one, then trim it from the ends.
        var doubled = sep + sep;
        while (result.Contains(doubled, StringComparison.Ordinal))
        {
            result = result.Replace(doubled, sep, StringComparison.Ordinal);
        }

        result = result.Trim(sepFirst).Trim('_');

        if (result.Length == 0)
        {
            return "column";
        }

        // A SQL identifier may not start with a digit.
        if (char.IsAsciiDigit(result[0]))
        {
            result = "_" + result;
        }

        return result;
    }

    private static bool MatchesAny(IReadOnlyList<string> patterns, string path)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        var normalized = NormalizeIndices(path);
        foreach (var pattern in patterns)
        {
            if (PathMatches(normalized, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rewrites concrete array indices to the wildcard form, e.g. <c>$.a[0].b</c> to <c>$.a[*].b</c>.</summary>
    private static string NormalizeIndices(string path)
    {
        if (path.IndexOf('[', StringComparison.Ordinal) < 0)
        {
            return path;
        }

        var sb = new StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close > i)
                {
                    var inner = path[(i + 1)..close];
                    sb.Append(inner.Length > 0 && (inner == "*" || inner.All(char.IsAsciiDigit)) ? "[*]" : path[i..(close + 1)]);
                    i = close;
                    continue;
                }
            }

            sb.Append(path[i]);
        }

        return sb.ToString();
    }

    /// <summary>Removes every <c>[...]</c> array subscript from a path fragment.</summary>
    private static string RemoveSubscripts(string value)
    {
        if (value.IndexOf('[', StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[')
            {
                var close = value.IndexOf(']', i);
                if (close > i)
                {
                    i = close;
                    continue;
                }
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exact match, or a single-<c>*</c> wildcard match (prefix/suffix), mirroring the original engine's
    /// lightweight matcher. Sufficient for the flatten rules; full recursive globbing is not needed here.
    /// </summary>
    private static bool PathMatches(string path, string pattern)
    {
        if (string.Equals(path, pattern, StringComparison.Ordinal))
        {
            return true;
        }

        var star = pattern.IndexOf('*', StringComparison.Ordinal);

        // A bracketed [*] is an array wildcard: the path is already index-normalized, so it matches only via
        // the exact comparison above. Treating it as a free glob would wrongly match a non-numeric subscript
        // (e.g. an object key literally named "a[0]"). Only a bare '*' (like $.raw_*) is a glob here.
        var isArrayWildcard = star > 0 && star + 1 < pattern.Length && pattern[star - 1] == '[' && pattern[star + 1] == ']';

        if (star >= 0 && !isArrayWildcard && pattern.IndexOf('*', star + 1) == -1)
        {
            var prefix = pattern[..star];
            var suffix = pattern[(star + 1)..];
            return path.Length >= prefix.Length + suffix.Length
                && path.StartsWith(prefix, StringComparison.Ordinal)
                && path.EndsWith(suffix, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Parses a comma-separated option value into a trimmed, non-empty path list.</summary>
    public static IReadOnlyList<string> SplitPaths(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Parses an array-handling option value (case- and separator-insensitive).</summary>
    public static JsonArrayHandling ParseArrayHandling(string? value)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

        return normalized switch
        {
            null or "" or "TOJSON" or "ASJSON" or "JSON" => JsonArrayHandling.ToJson,
            "FIRSTELEMENT" or "FIRST" => JsonArrayHandling.FirstElement,
            "JOIN" or "JOINCOMMA" => JsonArrayHandling.Join,
            "COUNT" => JsonArrayHandling.Count,
            "SKIP" => JsonArrayHandling.Skip,
            "EXPLODE" or "UNNEST" => JsonArrayHandling.Explode,
            _ => throw new SqlFlow.Core.SqlFlowException(
                $"Unknown arrayHandling '{value}'. Use to_json, first_element, join, count, skip, or explode."),
        };
    }

    /// <summary>Parses a <c>path=name;path=name</c> column-mapping option value.</summary>
    public static IReadOnlyDictionary<string, string> ParseColumnMappings(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq == pair.Length - 1)
            {
                throw new SqlFlow.Core.SqlFlowException(
                    $"Invalid columnMappings entry '{pair}'. Use 'jsonPath=columnName' separated by ';'.");
            }

            map[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        return map;
    }

    /// <summary>
    /// Parses the <c>pathAliases</c> option into a path-to-column lookup. The format is
    /// <c>columnName=$.path1|$.path2; columnName2=$.pathA|$.pathB</c>: each column lists the version-specific
    /// source paths that should feed it. Array indices in the paths are normalized to wildcards.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParsePathAliases(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (var group in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = group.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq == group.Length - 1)
            {
                throw new SqlFlow.Core.SqlFlowException(
                    $"Invalid pathAliases entry '{group}'. Use 'columnName=$.path1|$.path2' separated by ';'.");
            }

            var column = group[..eq].Trim();
            foreach (var path in group[(eq + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                map[NormalizeIndices(path)] = column;
            }
        }

        return map;
    }
}
