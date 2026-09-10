namespace SqlFlow.Sources.Json;

/// <summary>One output column of a flatten formula: its name and the source path it comes from.</summary>
/// <param name="Name">The (collision-resolved) SQL column name.</param>
/// <param name="SourcePath">The JSONPath whose value fills this column.</param>
/// <param name="IsJsonText">
/// True when the column holds a JSON string (an array kept whole, or a json-path subtree). Such values
/// can be arbitrarily large, so a generated formula must type them as a large text column.
/// </param>
public sealed record FlattenColumn(string Name, string SourcePath, bool IsJsonText);

/// <summary>
/// The "formula" for flattening a JSON dataset: the exact set of output columns and the source path each
/// comes from, with name collisions resolved. Two different paths can fold onto the same column name
/// (e.g. <c>$.vendor_id</c> and <c>$.vendor.id</c> both become <c>vendor_id</c>); under a plain flatten
/// the later one silently overwrites the earlier. The formula keeps the first at its natural name and
/// remaps the rest, so applying <see cref="CollisionMappings"/> as <c>columnMappings</c> makes the flatten
/// lossless.
/// </summary>
public sealed record JsonFlattenFormula(
    IReadOnlyList<FlattenColumn> Columns,
    IReadOnlyDictionary<string, string> CollisionMappings);

/// <summary>
/// Derives a <see cref="JsonFlattenFormula"/> from a path inventory and a flatten configuration. The
/// column projection mirrors the flattener for the default and to_json/join/count array handling: value
/// paths become columns, arrays become one column, objects flatten away, excluded subtrees and json-path
/// descendants are dropped, and a json path is kept as a single column. Array-element fields (paths with
/// <c>[*]</c>) are only realized by explosion or first-element handling, which this version does not
/// expand, so they are not listed as columns.
/// </summary>
public static class JsonFlattenFormulaBuilder
{
    public static JsonFlattenFormula Build(JsonPathInventory inventory, JsonFlattenConfig config)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(config);

        var columns = new List<FlattenColumn>();
        var collisionMappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var info in inventory.Paths)
        {
            if (!ProducesColumn(config, info))
            {
                continue;
            }

            var path = info.Path;

            // Converge paths that are the same field under a sometimes-array element (e.g. $.a.b from one
            // record and $.a[*].b from another): keep the first so they map to one column, not a collision.
            if (!seenFields.Add(StripSubscripts(path)))
            {
                continue;
            }

            var name = config.ColumnName(path);

            // Aliased paths intentionally share one output column (schema evolution): emit it once and
            // never remap, so version-specific paths converge instead of colliding.
            if (config.IsAliased(path))
            {
                if (used.Add(name))
                {
                    columns.Add(new FlattenColumn(name, path, IsJsonText(config, info)));
                }

                continue;
            }

            // Two unrelated paths can fold onto one name; the first keeps it, the rest get a distinct name
            // plus an explicit mapping so nothing is lost.
            var effective = Unique(name, used);
            if (!string.Equals(effective, name, StringComparison.Ordinal))
            {
                collisionMappings[path] = effective;
            }

            columns.Add(new FlattenColumn(effective, path, IsJsonText(config, info)));
        }

        return new JsonFlattenFormula(columns, collisionMappings);
    }

    /// <summary>Returns the name if free, otherwise the first <c>name_2</c>, <c>name_3</c>, ... that is not taken.</summary>
    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{name}_{suffix}";
            suffix++;
        }
        while (!used.Add(candidate));

        return candidate;
    }

    /// <summary>Removes every <c>[...]</c> subscript so a field is identified independent of array position.</summary>
    private static string StripSubscripts(string path)
    {
        if (path.IndexOf('[', StringComparison.Ordinal) < 0)
        {
            return path;
        }

        var sb = new System.Text.StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close > i)
                {
                    i = close;
                    continue;
                }
            }

            sb.Append(path[i]);
        }

        return sb.ToString();
    }

    private static bool ProducesColumn(JsonFlattenConfig config, JsonPathInfo info)
    {
        var path = info.Path;

        // The real flattener drops an excluded node's whole subtree on recursion, so a descendant of an
        // excluded path never becomes a column either; the flat inventory must replicate that here.
        if (config.IsExcluded(path) || IsDescendantOf(config.ExcludePaths, path) || !config.IsIncluded(path))
        {
            return false;
        }

        // A json path is kept as one column; its descendants are folded into it.
        if (config.IsJsonString(path))
        {
            return true;
        }

        if (IsDescendantOf(config.JsonPaths, path))
        {
            return false;
        }

        // An array-element field ($.items[*].sku) is realized as a column when its array levels are exploded,
        // or when first_element flattens the array's first element in place (same index-stripped column name).
        if (path.Contains("[*]", StringComparison.Ordinal) && !LeafRealized(config, path))
        {
            return false;
        }

        return info.Kind switch
        {
            JsonNodeKind.Object => false,
            // An exploded array is consumed into rows, not kept as a column; otherwise to_json/join/count
            // make one column (skip/first_element do not).
            JsonNodeKind.Array => !config.ShouldExplode(path)
                && config.ArrayHandling is not (JsonArrayHandling.Skip or JsonArrayHandling.FirstElement),
            _ => true,
        };
    }

    /// <summary>
    /// True when a <c>[*]</c> element field becomes a column: every array level is exploded, or first_element
    /// flattens the first element of each level in place (which yields the same index-stripped column names).
    /// For a heterogeneous first_element array this can list fields from beyond the first element, an accepted
    /// imprecision of the preview for that uncommon combination.
    /// </summary>
    private static bool LeafRealized(JsonFlattenConfig config, string path)
        => config.ArrayHandling == JsonArrayHandling.FirstElement || AllArrayLevelsExploded(config, path);

    /// <summary>True when every <c>[*]</c> array level in the path is an explode target (so the leaf is realized).</summary>
    private static bool AllArrayLevelsExploded(JsonFlattenConfig config, string path)
    {
        if (config.ArrayHandling == JsonArrayHandling.Explode)
        {
            return true;
        }

        var idx = path.IndexOf("[*]", StringComparison.Ordinal);
        while (idx >= 0)
        {
            if (!config.ShouldExplode(path[..idx]))
            {
                return false;
            }

            idx = path.IndexOf("[*]", idx + 3, StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>True when the column carries a JSON string: a json-path subtree, or an array kept whole.</summary>
    private static bool IsJsonText(JsonFlattenConfig config, JsonPathInfo info)
        => config.IsJsonString(info.Path)
            || (info.Kind == JsonNodeKind.Array && config.ArrayHandling == JsonArrayHandling.ToJson);

    /// <summary>True if <paramref name="path"/> is nested under one of the (literal) ancestor patterns.</summary>
    private static bool IsDescendantOf(IReadOnlyList<string> ancestors, string path)
    {
        foreach (var ancestor in ancestors)
        {
            // Prefix-star excludes like "$.raw_*" already match descendants via the node check, so only
            // literal ancestors need a structural prefix test here.
            if (ancestor.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }

            if (path.Length > ancestor.Length
                && path.StartsWith(ancestor, StringComparison.Ordinal)
                && (path[ancestor.Length] == '.' || path[ancestor.Length] == '['))
            {
                return true;
            }
        }

        return false;
    }
}
