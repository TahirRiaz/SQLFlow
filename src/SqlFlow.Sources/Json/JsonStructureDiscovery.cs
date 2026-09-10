using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlFlow.Sources.Json;

/// <summary>The JSONPath structure discovered across a set of sampled records.</summary>
/// <param name="RecordsScanned">How many records were inspected.</param>
/// <param name="AllPaths">Every distinct JSONPath found, in first-seen order.</param>
/// <param name="Structures">The distinct record shapes (schema versions), most common first.</param>
public sealed record JsonDiscoveryResult(
    int RecordsScanned,
    IReadOnlyList<string> AllPaths,
    IReadOnlyList<JsonStructure> Structures)
{
    /// <summary>How many files contributed to the sample.</summary>
    public int FilesScanned { get; init; }
}

/// <summary>One distinct record shape: a set of paths shared by <see cref="RecordCount"/> records.</summary>
/// <param name="Fingerprint">Stable hash of the sorted path set.</param>
/// <param name="RecordCount">Number of sampled records with exactly this shape.</param>
/// <param name="Paths">The paths that make up this shape, in first-seen order.</param>
public sealed record JsonStructure(string Fingerprint, int RecordCount, IReadOnlyList<string> Paths);

/// <summary>
/// Extracts the JSONPath structure of JSON records, the read-only counterpart to the flattener used for
/// the <c>discover</c> command. It mirrors the original delta-forge discovery: collect the leaf and
/// array-bearing paths of each record (as <c>$.a.b[*].c</c>), union them in discovery order, and group
/// records by the fingerprint of their path set so schema drift across files is visible.
/// </summary>
public static class JsonStructureDiscovery
{
    /// <summary>The JSONPaths of a single record: leaves and arrays, with intermediate objects skipped.</summary>
    public static IReadOnlyList<string> ExtractPaths(JsonElement record, int maxDepth)
    {
        var paths = new List<string>();
        ExtractRecursive(record, "$", 0, maxDepth, paths);
        return paths;
    }

    /// <summary>Aggregates the structure of many records into the unified path set and the distinct shapes.</summary>
    public static JsonDiscoveryResult Discover(IEnumerable<JsonElement> records, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Aggregate(records.Select(record => ExtractPaths(record, maxDepth)));
    }

    /// <summary>
    /// Aggregates already-extracted per-record path lists. Kept separate from <see cref="ExtractPaths"/> so
    /// callers that read records from many files (each backed by a short-lived parser) can extract eagerly
    /// per record and aggregate the small path lists afterwards.
    /// </summary>
    public static JsonDiscoveryResult Aggregate(IEnumerable<IReadOnlyList<string>> perRecordPaths)
    {
        ArgumentNullException.ThrowIfNull(perRecordPaths);

        var allPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var structures = new Dictionary<string, StructureAccumulator>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var paths in perRecordPaths)
        {
            scanned++;

            foreach (var path in paths)
            {
                if (seen.Add(path))
                {
                    allPaths.Add(path);
                }
            }

            var fingerprint = Fingerprint(paths);
            if (structures.TryGetValue(fingerprint, out var accumulator))
            {
                accumulator.Count++;
            }
            else
            {
                structures[fingerprint] = new StructureAccumulator { Count = 1, Paths = paths };
            }
        }

        var ordered = structures
            .Select(kvp => new JsonStructure(kvp.Key, kvp.Value.Count, kvp.Value.Paths))
            .OrderByDescending(s => s.RecordCount)
            .ThenBy(s => s.Fingerprint, StringComparer.Ordinal)
            .ToList();

        return new JsonDiscoveryResult(scanned, allPaths, ordered);
    }

    private static void ExtractRecursive(JsonElement value, string path, int depth, int maxDepth, List<string> paths)
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

                    // A non-empty object is an intermediate node, not a column; only its descendants count.
                    var isPopulatedObject = property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.EnumerateObject().MoveNext();

                    if (!isPopulatedObject)
                    {
                        paths.Add(childPath);
                    }

                    ExtractRecursive(property.Value, childPath, depth + 1, maxDepth, paths);
                }

                break;

            case JsonValueKind.Array:
                foreach (var first in value.EnumerateArray())
                {
                    ExtractRecursive(first, $"{path}[*]", depth + 1, maxDepth, paths);
                    break; // the first element represents the array's element shape
                }

                break;
        }
    }

    private static string Fingerprint(IReadOnlyList<string> paths)
    {
        var sorted = paths.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var path in sorted)
        {
            builder.Append(path).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private sealed class StructureAccumulator
    {
        public int Count { get; set; }
        public IReadOnlyList<string> Paths { get; set; } = [];
    }
}
