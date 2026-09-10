using System.Text;

namespace SqlFlow.Sources.Xml;

/// <summary>How a repeating element (several same-named siblings, XML's analog of a JSON array) becomes a value.</summary>
public enum XmlRepeatHandling
{
    /// <summary>Keep the repeating elements as one XML-fragment string column (lossless). The default.</summary>
    ToXml,

    /// <summary>Flatten the first element in place (its content becomes columns under the element's path).</summary>
    FirstElement,

    /// <summary>Flatten the last element in place.</summary>
    LastElement,

    /// <summary>Join the elements' text with a separator into one string.</summary>
    Join,

    /// <summary>Emit the element count as the column value.</summary>
    Count,

    /// <summary>Drop the repeating elements entirely (no column).</summary>
    Skip,

    /// <summary>Emit one output row per element (cross-product with other exploded paths).</summary>
    Explode,
}

/// <summary>
/// Declarative rules for turning a nested XML document into flat table columns, addressed by XPath-like
/// expressions relative to the row (record) element. The XML counterpart of the JSON flattener: the same
/// include / exclude / keep-as-string / explode / alias semantics and path-based naming, adapted to XML's
/// model - attributes (addressed as <c>/@name</c>), element nesting (<c>/a/b</c>), namespaces (stripped by
/// default), and repeating sibling elements (XML's arrays). Every value is a raw string; the existing
/// type-inference step types them afterward.
/// </summary>
public sealed record XmlFlattenConfig
{
    /// <summary>
    /// XPath selecting the row (record) elements. Empty or <c>/*</c> = each direct child element of the root
    /// is a record (the common wrapped-rows shape). <c>.</c> or <c>/</c> = the root element itself is one
    /// record. Any other value is evaluated as an XPath against the namespace-stripped document.
    /// </summary>
    public string RowXPath { get; init; } = string.Empty;

    /// <summary>Whitelist: when non-empty, only these paths (and the chain needed to reach them) are flattened.</summary>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];

    /// <summary>Paths whose subtree is dropped from the output (no column is produced).</summary>
    public IReadOnlyList<string> ExcludePaths { get; init; } = [];

    /// <summary>Paths kept verbatim as a single XML-fragment string column instead of being flattened into children.</summary>
    public IReadOnlyList<string> XmlPaths { get; init; } = [];

    /// <summary>Repeating-element paths to explode: each element becomes its own output row. Multiple paths cross-product.</summary>
    public IReadOnlyList<string> ExplodePaths { get; init; } = [];

    /// <summary>
    /// Trim surrounding whitespace from every element's text (the default). Turn it OFF when the whitespace is
    /// part of the value: a source whose text is a rendered label can carry a deliberate leading space, and a
    /// downstream contract built on that label then depends on it surviving. Trimming is the right default for
    /// XML, where indentation is not data, so this is opt-out rather than opt-in.
    /// </summary>
    public bool TrimText { get; init; } = true;

    /// <summary>Schema-evolution aliases: a normalized source path mapped to the output column it should feed.</summary>
    public IReadOnlyDictionary<string, string> PathAliasColumns { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Explicit XPath to column-name overrides.</summary>
    public IReadOnlyDictionary<string, string> ColumnMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Maximum nesting depth flattened into columns; deeper elements become XML-fragment strings.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>Bound on how many rows one record may explode into before the read fails loudly.</summary>
    public int MaxRowsPerRecord { get; init; } = XmlPathFlattener.DefaultMaxRowsPerRecord;

    /// <summary>Separator joining nested element/attribute names into a column name (e.g. <c>address_city</c>).</summary>
    public string Separator { get; init; } = "_";

    /// <summary>How repeating sibling elements not handled by an explicit rule become a column value.</summary>
    public XmlRepeatHandling RepeatHandling { get; init; } = XmlRepeatHandling.ToXml;

    /// <summary>Separator used when <see cref="RepeatHandling"/> is <see cref="XmlRepeatHandling.Join"/>.</summary>
    public string JoinSeparator { get; init; } = ",";

    /// <summary>Whether XML attributes become columns. Default true.</summary>
    public bool IncludeAttributes { get; init; } = true;

    /// <summary>Prefix applied to an attribute's name in its column (default <c>@</c>); set e.g. <c>attr_</c> to
    /// keep an attribute distinct from a same-named element on the same parent.</summary>
    public string AttributePrefix { get; init; } = "@";

    /// <summary>True if this path's subtree should be dropped from the output.</summary>
    public bool IsExcluded(string path) => MatchesAny(ExcludePaths, path);

    /// <summary>True if this path should be kept as a single XML-fragment column.</summary>
    public bool IsXmlString(string path) => MatchesAny(XmlPaths, path);

    /// <summary>True if this repeating-element path should be exploded into one row per element.</summary>
    public bool ShouldExplode(string path)
        => RepeatHandling == XmlRepeatHandling.Explode || MatchesAny(ExplodePaths, path);

    /// <summary>True if this path feeds an aliased (schema-evolution) output column.</summary>
    public bool IsAliased(string path)
        => PathAliasColumns.Count > 0 && PathAliasColumns.ContainsKey(NormalizeIndices(path));

    /// <summary>
    /// The distinct, sanitized output column names that aliases feed. Reserved up front by the flattener so a
    /// natural field whose own name resolves to an alias target de-collides into its own column (regardless of
    /// document order) instead of colliding with - and dropping - the aliased value.
    /// </summary>
    public IReadOnlyCollection<string> AliasTargetColumns()
    {
        if (PathAliasColumns.Count == 0)
        {
            return [];
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in PathAliasColumns.Values)
        {
            set.Add(Sanitize(target));
        }

        return set;
    }

    /// <summary>
    /// True if traversal should descend through, or emit, this path. XML-string paths are implicitly included.
    /// With no include list everything is included; otherwise a path is included when it matches an include
    /// pattern, is a descendant of one, or is an ancestor on the way to one.
    /// </summary>
    public bool IsIncluded(string path)
    {
        if (IsXmlString(path))
        {
            return true;
        }

        if (IncludePaths.Count == 0)
        {
            return true;
        }

        var normalized = NormalizeIndices(path);
        foreach (var pattern in IncludePaths)
        {
            if (PathMatches(normalized, pattern)
                || IsAtOrUnder(normalized, pattern)
                || pattern.StartsWith(normalized + "/", StringComparison.Ordinal)
                || pattern.StartsWith(normalized + "[", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The SQL column name for an XPath: an explicit mapping or alias if present, else the path with its
    /// leading slash, attribute marker, slashes, and any repetition subscripts folded onto the separator and
    /// sanitized to a valid identifier. Casing is preserved.
    /// </summary>
    public string ColumnName(string path)
    {
        // Column mappings and aliases are addressed independently of repetition, so both are keyed by the
        // index-normalized path ([0]/[1] folded to [*]). The runtime sees concrete indices (/line[0]/sku) while
        // the user writes the wildcard form (/line[*]/sku) the discover output prints; normalizing both ends
        // keeps a mapping or alias on a repeating element applied at load, not silently dropped.
        var normalized = NormalizeIndices(path);

        if (ColumnMappings.Count > 0
            && ColumnMappings.TryGetValue(normalized, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
        {
            return Sanitize(mapped);
        }

        if (PathAliasColumns.Count > 0
            && PathAliasColumns.TryGetValue(normalized, out var alias)
            && !string.IsNullOrWhiteSpace(alias))
        {
            return Sanitize(alias);
        }

        var work = path.TrimStart('/');

        // An attribute marker (@name) takes the configured prefix so it can stay distinct from a same-named
        // element; repetition subscripts ([0], [*]) are removed so a row is one stable column per field.
        work = work.Replace("@", AttributePrefix, StringComparison.Ordinal);
        work = RemoveSubscripts(work).Replace('/', Separator.Length > 0 ? Separator[0] : '_');

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

    /// <summary>True if <paramref name="path"/> equals <paramref name="ancestor"/> or is nested under it at a boundary.</summary>
    private static bool IsAtOrUnder(string path, string ancestor)
        => path.StartsWith(ancestor, StringComparison.Ordinal)
            && (path.Length == ancestor.Length || path[ancestor.Length] is '/' or '[');

    private static bool PathMatches(string path, string pattern)
    {
        if (string.Equals(path, pattern, StringComparison.Ordinal))
        {
            return true;
        }

        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        var isRepeatWildcard = star > 0 && star + 1 < pattern.Length && pattern[star - 1] == '[' && pattern[star + 1] == ']';

        if (star >= 0 && !isRepeatWildcard && pattern.IndexOf('*', star + 1) == -1)
        {
            var prefix = pattern[..star];
            var suffix = pattern[(star + 1)..];
            return path.Length >= prefix.Length + suffix.Length
                && path.StartsWith(prefix, StringComparison.Ordinal)
                && path.EndsWith(suffix, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Rewrites concrete repetition indices to the wildcard form, e.g. <c>/a/b[0]/c</c> to <c>/a/b[*]/c</c>.</summary>
    public static string NormalizeIndices(string path)
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

    /// <summary>Parses a comma-separated option value into a trimmed, non-empty path list.</summary>
    public static IReadOnlyList<string> SplitPaths(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Parses a repeat-handling option value (case- and separator-insensitive).</summary>
    public static XmlRepeatHandling ParseRepeatHandling(string? value)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

        return normalized switch
        {
            null or "" or "TOXML" or "ASXML" or "XML" or "TOJSON" => XmlRepeatHandling.ToXml,
            "FIRSTELEMENT" or "FIRST" => XmlRepeatHandling.FirstElement,
            "LASTELEMENT" or "LAST" => XmlRepeatHandling.LastElement,
            "JOIN" or "JOINCOMMA" => XmlRepeatHandling.Join,
            "COUNT" => XmlRepeatHandling.Count,
            "SKIP" => XmlRepeatHandling.Skip,
            "EXPLODE" or "UNNEST" => XmlRepeatHandling.Explode,
            _ => throw new SqlFlow.Core.SqlFlowException(
                $"Unknown repeatHandling '{value}'. Use to_xml, first_element, last_element, join, count, skip, or explode."),
        };
    }

    /// <summary>Parses a <c>path=name;path=name</c> column-mapping option value (keys index-normalized).</summary>
    public static IReadOnlyDictionary<string, string> ParseColumnMappings(string? value)
    {
        var raw = ParsePairs(value, "columnMappings", "xpath=columnName");
        if (raw.Count == 0)
        {
            return raw;
        }

        // Store keys in the index-normalized form so a mapping on a repeating path matches the concrete
        // [N] indices the runtime produces (see ColumnName).
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, mapped) in raw)
        {
            map[NormalizeIndices(key)] = mapped;
        }

        return map;
    }

    /// <summary>Parses the <c>pathAliases</c> option into a path-to-column lookup (<c>col=/a|/b; col2=/c|/d</c>).</summary>
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
                    $"Invalid pathAliases entry '{group}'. Use 'columnName=/path1|/path2' separated by ';'.");
            }

            var column = group[..eq].Trim();
            foreach (var path in group[(eq + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                map[NormalizeIndices(path)] = column;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> ParsePairs(string? value, string option, string shape)
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
                throw new SqlFlow.Core.SqlFlowException($"Invalid {option} entry '{pair}'. Use '{shape}' separated by ';'.");
            }

            map[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        return map;
    }
}
