using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SqlFlow.Sources.Json;

/// <summary>What a JSONPath points at: a value (leaf), an object, or an array.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "JSON's data model names these object/array/value; matching the spec reads clearer than a synonym.")]
public enum JsonNodeKind
{
    /// <summary>A scalar leaf (string, number, bool, null) that becomes a column.</summary>
    Value,

    /// <summary>An object container; a valid target for rootPath, jsonPaths, or excludePaths.</summary>
    Object,

    /// <summary>An array container; becomes a column under arrayHandling, or a target for jsonPaths/excludePaths.</summary>
    Array,
}

/// <summary>A path discovered in a record together with what it points at.</summary>
/// <param name="Path">The JSONPath, e.g. <c>$.vendor.name</c> or <c>$.details[*].track_id</c>.</param>
/// <param name="Kind">Whether the path is a value, an object, or an array.</param>
public readonly record struct JsonPathNode(string Path, JsonNodeKind Kind);

/// <summary>One path in the aggregated inventory, with how many sampled records contained it.</summary>
/// <param name="Path">The JSONPath.</param>
/// <param name="Kind">Whether the path is a value, an object, or an array.</param>
/// <param name="RecordCount">Number of sampled records in which this path appeared.</param>
public sealed record JsonPathInfo(string Path, JsonNodeKind Kind, int RecordCount);

/// <summary>Every addressable JSONPath found across a sample, including object and array containers.</summary>
/// <param name="RecordsScanned">How many records were inspected.</param>
/// <param name="Paths">Each distinct path, in first-seen order, with its kind and record count.</param>
public sealed record JsonPathInventory(int RecordsScanned, IReadOnlyList<JsonPathInfo> Paths)
{
    /// <summary>How many files contributed to the sample.</summary>
    public int FilesScanned { get; init; }
}

/// <summary>
/// Builds a full inventory of the addressable JSONPaths in a set of records. Unlike the column-oriented
/// discovery (which lists only the leaves that become columns), this also surfaces the object and array
/// container paths, since those are the valid targets for rootPath, jsonPaths, and excludePaths. It powers
/// the <c>paths</c> command, which answers "what paths exist in this file?".
/// </summary>
public static class JsonPathInventoryBuilder
{
    /// <summary>Every addressable path in one record, with its kind, in document order.</summary>
    public static IReadOnlyList<JsonPathNode> ExtractTypedPaths(JsonElement record, int maxDepth)
    {
        var nodes = new List<JsonPathNode>();
        Walk(record, "$", 0, maxDepth, nodes);
        return nodes;
    }

    /// <summary>Aggregates per-record path lists into the unified inventory (first-seen order, per-path counts).</summary>
    public static JsonPathInventory Build(IEnumerable<IReadOnlyList<JsonPathNode>> perRecordNodes)
    {
        ArgumentNullException.ThrowIfNull(perRecordNodes);

        var order = new List<string>();
        var kinds = new Dictionary<string, JsonNodeKind>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var nodes in perRecordNodes)
        {
            scanned++;
            var countedThisRecord = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in nodes)
            {
                if (kinds.TryGetValue(node.Path, out var existing))
                {
                    // Heterogeneous data: prefer the more structured kind so a container is never hidden
                    // behind a value reading from another record.
                    if (Rank(node.Kind) > Rank(existing))
                    {
                        kinds[node.Path] = node.Kind;
                    }
                }
                else
                {
                    kinds[node.Path] = node.Kind;
                    counts[node.Path] = 0;
                    order.Add(node.Path);
                }

                // Count each path at most once per record, even if (pathologically) it recurs.
                if (countedThisRecord.Add(node.Path))
                {
                    counts[node.Path]++;
                }
            }
        }

        var paths = order
            .Select(path => new JsonPathInfo(path, kinds[path], counts[path]))
            .ToList();

        return new JsonPathInventory(scanned, paths);
    }

    private static void Walk(JsonElement value, string path, int depth, int maxDepth, List<JsonPathNode> nodes)
    {
        if (depth > maxDepth)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
                    nodes.Add(new JsonPathNode(childPath, KindOf(property.Value)));
                    Walk(property.Value, childPath, depth + 1, maxDepth, nodes);
                }

                break;

            case JsonValueKind.Array:
            {
                // Emit one [*] element node (so an exploded scalar array still surfaces its column), then
                // recurse into EVERY object/array element so a heterogeneous array's later-element fields are
                // captured too - mirroring the runtime ExplodeSchema, which visits all elements.
                var emittedElementNode = false;
                foreach (var element in value.EnumerateArray())
                {
                    var elementPath = $"{path}[*]";
                    if (!emittedElementNode)
                    {
                        nodes.Add(new JsonPathNode(elementPath, KindOf(element)));
                        emittedElementNode = true;
                    }

                    if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Walk(element, elementPath, depth + 1, maxDepth, nodes);
                    }
                }

                break;
            }
        }
    }

    private static JsonNodeKind KindOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonNodeKind.Object,
        JsonValueKind.Array => JsonNodeKind.Array,
        _ => JsonNodeKind.Value,
    };

    private static int Rank(JsonNodeKind kind) => kind switch
    {
        JsonNodeKind.Object => 3,
        JsonNodeKind.Array => 2,
        _ => 1,
    };
}
