using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// A focused JSONPath reader for the acquisition engine: enough to read a token out of an auth response, a cursor
/// or next-id out of a page, the ids out of a discovery response, and the record array used to detect an empty
/// page. Supports <c>$</c> root, dotted properties (<c>$.data.token</c>), array indices (<c>$.items[0]</c>), and
/// the wildcard (<c>$.data[*].id</c>, <c>$[*].BikeId</c>). A leading <c>$</c> is optional.
/// </summary>
public static class JsonPathReader
{
    private static readonly string[] CommonRecordKeys = ["data", "items", "results", "records", "value"];

    /// <summary>All string-projected values a (possibly wildcard) path selects, skipping nulls/absent nodes.</summary>
    public static IReadOnlyList<string> SelectValues(JsonElement root, string path)
    {
        var values = new List<string>();
        foreach (var element in Evaluate(root, Parse(path)))
        {
            if (Stringify(element) is { } s)
            {
                values.Add(s);
            }
        }

        return values;
    }

    /// <summary>The first string-projected value a path selects, or null when the path matches nothing.</summary>
    public static string? SelectValue(JsonElement root, string path)
    {
        foreach (var element in Evaluate(root, Parse(path)))
        {
            if (Stringify(element) is { } s)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Every element a (possibly wildcard) path selects, in document order. Used to fan out over records
    /// when each one contributes several bound variables rather than a single id.</summary>
    public static IReadOnlyList<JsonElement> SelectElements(JsonElement root, string path)
        => Evaluate(root, Parse(path)).ToList();

    /// <summary>The first element a path selects, or null.</summary>
    public static JsonElement? SelectElement(JsonElement root, string path)
    {
        foreach (var element in Evaluate(root, Parse(path)))
        {
            return element;
        }

        return null;
    }

    /// <summary>
    /// Counts the records in a page for empty-page detection. When <paramref name="recordsPath"/> is set it is the
    /// array; otherwise the array is the whole body (if it is an array) or the first common wrapper key. A non-array
    /// object body counts as one record; a missing/absent array counts as zero.
    /// </summary>
    public static int CountRecords(JsonElement root, string? recordsPath)
    {
        var array = LocateRecordArray(root, recordsPath);
        if (array is not { } element)
        {
            return root.ValueKind == JsonValueKind.Object ? 1 : 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Null or JsonValueKind.Undefined => 0,
            _ => 1,
        };
    }

    /// <summary>The record array for a page, honoring an explicit path or auto-locating the common wrapper keys.</summary>
    public static JsonElement? LocateRecordArray(JsonElement root, string? recordsPath)
    {
        if (!string.IsNullOrWhiteSpace(recordsPath))
        {
            return SelectElement(root, recordsPath);
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in CommonRecordKeys)
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>The lexicographic max of a column across the record array, for a response watermark. Null when absent.</summary>
    public static string? MaxColumn(JsonElement root, string column, string? recordsPath)
    {
        var array = LocateRecordArray(root, recordsPath);
        if (array is not { ValueKind: JsonValueKind.Array } element)
        {
            return null;
        }

        string? max = null;
        foreach (var record in element.EnumerateArray())
        {
            if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty(column, out var value)
                && Stringify(value) is { } s && (max is null || string.CompareOrdinal(s, max) > 0))
            {
                max = s;
            }
        }

        return max;
    }

    private static IEnumerable<JsonElement> Evaluate(JsonElement root, IReadOnlyList<PathSegment> segments)
    {
        IEnumerable<JsonElement> current = [root];
        foreach (var segment in segments)
        {
            current = Step(current, segment).ToList();
        }

        return current;
    }

    private static IEnumerable<JsonElement> Step(IEnumerable<JsonElement> elements, PathSegment segment)
    {
        foreach (var element in elements)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Property when element.ValueKind == JsonValueKind.Object
                                               && element.TryGetProperty(segment.Name!, out var property):
                    yield return property;
                    break;
                case SegmentKind.Index when element.ValueKind == JsonValueKind.Array
                                            && segment.Index < element.GetArrayLength():
                    yield return element[segment.Index];
                    break;
                case SegmentKind.Wildcard when element.ValueKind == JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        yield return item;
                    }

                    break;
                case SegmentKind.Wildcard when element.ValueKind == JsonValueKind.Object:
                    foreach (var item in element.EnumerateObject())
                    {
                        yield return item.Value;
                    }

                    break;
            }
        }
    }

    private static string? Stringify(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };

    private static IReadOnlyList<PathSegment> Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = new List<PathSegment>();
        var i = 0;
        if (path[0] == '$')
        {
            i = 1;
        }

        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = path.IndexOf(']', i + 1);
                if (close < 0)
                {
                    throw new SqlFlowException($"Unterminated '[' in JSON path '{path}'.");
                }

                var inner = path[(i + 1)..close].Trim().Trim('\'', '"');
                segments.Add(inner == "*"
                    ? PathSegment.OfWildcard()
                    : int.TryParse(inner, out var index)
                        ? PathSegment.OfIndex(index)
                        : PathSegment.OfProperty(inner));
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[')
            {
                i++;
            }

            var name = path[start..i];
            segments.Add(name == "*" ? PathSegment.OfWildcard() : PathSegment.OfProperty(name));
        }

        return segments;
    }

    private enum SegmentKind
    {
        Property,
        Index,
        Wildcard,
    }

    private readonly record struct PathSegment(SegmentKind Kind, string? Name, int Index)
    {
        public static PathSegment OfProperty(string name) => new(SegmentKind.Property, name, 0);

        public static PathSegment OfIndex(int index) => new(SegmentKind.Index, null, index);

        public static PathSegment OfWildcard() => new(SegmentKind.Wildcard, null, 0);
    }
}
