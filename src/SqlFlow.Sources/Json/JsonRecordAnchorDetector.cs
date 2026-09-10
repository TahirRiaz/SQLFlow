using System.Text;
using System.Text.Json;

namespace SqlFlow.Sources.Json;

/// <summary>
/// Statistics-driven detection of the record-collection anchor in JSON documents. Most real-world JSON APIs
/// do not hand back a bare array of records; they wrap it in an envelope, for example
/// <c>{ "total_count": 867, "incomplete_results": false, "items": [ {..}, {..} ] }</c>. Flattening such a
/// document at the root yields exactly one row whose <c>items</c> column is the whole array serialized to a
/// JSON blob: the records stay trapped in one cell, the opposite of what an analyst wants (one row per record).
///
/// This detector finds the JSONPath whose array elements should become the table's rows. It works structurally,
/// from per-array statistics gathered across every sampled document: how many elements each array holds, how
/// many of them are objects, how homogeneous the element shapes are, and how deep the array sits. The dominant
/// array of objects wins. When nothing qualifies (a plain configuration object, a single nested sub-record, a
/// root-level array the caller already flattens, a document that genuinely is one row) it returns null and the
/// document-root grain stands.
///
/// The detector only proposes a path; <see cref="JsonSourceReader"/> acts on it by feeding it as the discovery
/// root path, which <see cref="JsonRecordReader"/> already honors by navigating into the array and emitting one
/// record per element. It mirrors the delta-forge <c>record_anchor</c> translator so DISCOVER produces one row
/// per record without a hand-written flatten config, and an explicit rootPath always wins over detection.
/// </summary>
public sealed class JsonRecordAnchorDetector
{
    /// <summary>Aggregated statistics for one array path, unioned across every sampled document.</summary>
    private sealed class ArrayStats
    {
        /// <summary>Total element count summed across every occurrence and sample.</summary>
        public long Total;

        /// <summary>How many of those elements were JSON objects (record-like).</summary>
        public long Objects;

        /// <summary>Distinct structural shapes among the elements. One shape means a perfectly homogeneous,
        /// table-like array; many shapes means a grab bag that makes a poor row grain.</summary>
        public readonly HashSet<string> Shapes = new(StringComparer.Ordinal);

        /// <summary>Field count of the richest object element (tie-break toward wider records).</summary>
        public int MaxFields;
    }

    private readonly SortedDictionary<string, ArrayStats> _stats = new(StringComparer.Ordinal);
    private readonly int _maxDepth;
    private bool _firstSampleSeen;
    private bool _firstIsObject;

    /// <summary>The top-level keys of the first sample whose value is an object or array (i.e. non-scalar).
    /// Used to recognize the scalar-envelope signature that justifies descending into a single-element array.</summary>
    private readonly HashSet<string> _firstNonScalarTopLevelKeys = new(StringComparer.Ordinal);

    /// <param name="maxDepth">Bounds the structural walk so a pathological document cannot drive unbounded
    /// recursion. Values below 1 are treated as the default depth.</param>
    public JsonRecordAnchorDetector(int maxDepth) => _maxDepth = maxDepth < 1 ? 10 : maxDepth;

    /// <summary>
    /// Accumulates one top-level document's array statistics into the running aggregate. The document is the
    /// whole parsed value (a JSON object, a root array, or a scalar), NOT an already-expanded record. Must be
    /// called while <paramref name="document"/> is still valid (before its backing <see cref="JsonDocument"/>
    /// is disposed); the walk copies out only the small aggregate it needs.
    /// </summary>
    public void Accumulate(JsonElement document)
    {
        if (!_firstSampleSeen)
        {
            _firstSampleSeen = true;
            _firstIsObject = document.ValueKind == JsonValueKind.Object;
            if (_firstIsObject)
            {
                foreach (var property in document.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        _firstNonScalarTopLevelKeys.Add(property.Name);
                    }
                }
            }
        }

        // Only an object can hide an envelope. A root array is already a record collection (the reader flattens
        // its elements directly); a scalar root is not a record source. Both leave the aggregate untouched.
        if (document.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var path = new StringBuilder("$");
        CollectArrayStats(document, path, 0);
    }

    /// <summary>
    /// Returns the JSONPath whose array elements should become table rows (for example <c>$.items</c> or a
    /// deeper <c>$.data.results</c>), or null to keep the document-root grain. Call after every sample has been
    /// accumulated.
    /// </summary>
    public string? Detect()
    {
        // Gate on the first sample so a homogeneous corpus takes the fast, predictable path: a non-object first
        // document (root array or scalar) yields no envelope to unwrap.
        if (!_firstSampleSeen || !_firstIsObject)
        {
            return null;
        }

        // Candidate = an array whose elements are predominantly objects (obj_ratio >= 0.5). Arrays of scalars
        // (a tags or ids list) are never a row grain, however long they are.
        var candidates = new List<Candidate>();
        foreach (var (path, stats) in _stats)
        {
            if (stats.Objects >= 1 && stats.Total >= 1 && stats.Objects * 2 >= stats.Total)
            {
                candidates.Add(new Candidate(path, stats.Total, SegmentDepth(path), stats.Shapes.Count, stats.MaxFields));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Rank: most rows first, then shallowest, then most homogeneous, then richest records, then path order
        // for a fully deterministic tie-break.
        candidates.Sort(static (a, b) =>
        {
            var c = b.Total.CompareTo(a.Total);
            if (c != 0) return c;
            c = a.Depth.CompareTo(b.Depth);
            if (c != 0) return c;
            c = a.DistinctShapes.CompareTo(b.DistinctShapes);
            if (c != 0) return c;
            c = b.MaxFields.CompareTo(a.MaxFields);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Path, b.Path);
        });

        var best = candidates[0];

        // Descend only when the array clearly out-rows the document root. A 2+-element record array always beats
        // the single row a bare object would yield. A 1-element array is ambiguous (it could be an incidental
        // nested sub-record), so it only wins when it sits at the top level of an otherwise all-scalar envelope:
        // the { total_count, incomplete_results, items: [ {..} ] } shape with a single search result.
        var descend = best.Total >= 2
            || (best.Total == 1 && best.Depth == 1 && IsScalarEnvelope(best.Path));

        return descend ? best.Path : null;
    }

    /// <summary>Convenience for one-shot detection over an in-memory set of top-level documents.</summary>
    public static string? Detect(IEnumerable<JsonElement> documents, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var detector = new JsonRecordAnchorDetector(maxDepth);
        foreach (var document in documents)
        {
            detector.Accumulate(document);
        }

        return detector.Detect();
    }

    /// <summary>One viable record-collection array, flattened to its ranking inputs.</summary>
    private readonly record struct Candidate(string Path, long Total, int Depth, int DistinctShapes, int MaxFields);

    /// <summary>
    /// Walks <paramref name="value"/>, accumulating per-array statistics keyed by JSONPath. Array element
    /// children are recorded at the array's own path (no <c>[idx]</c> segment), mirroring the flattener's
    /// transparent-array convention so the paths the detector reasons about match the paths the reader honors as
    /// a root path. A homogeneous array costs one descent: elements are deduped by structural shape first.
    /// </summary>
    private void CollectArrayStats(JsonElement value, StringBuilder path, int depth)
    {
        if (depth > _maxDepth)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var saved = path.Length;
                    path.Append('.').Append(property.Name);
                    CollectArrayStats(property.Value, path, depth + 1);
                    path.Length = saved;
                }

                break;

            case JsonValueKind.Array:
            {
                var key = path.ToString();
                if (!_stats.TryGetValue(key, out var entry))
                {
                    entry = new ArrayStats();
                    _stats[key] = entry;
                }

                foreach (var element in value.EnumerateArray())
                {
                    entry.Total++;
                    entry.Shapes.Add(ShapeSignature(element));
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        entry.Objects++;
                        var fields = CountProperties(element);
                        if (fields > entry.MaxFields)
                        {
                            entry.MaxFields = fields;
                        }
                    }
                }

                // Descend into one representative per distinct element shape to discover nested record arrays.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in value.EnumerateArray())
                {
                    if (!seen.Add(ShapeSignature(element)))
                    {
                        continue;
                    }

                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Object:
                            // Transparent-array convention: an element object's children live at the array path.
                            foreach (var property in element.EnumerateObject())
                            {
                                var saved = path.Length;
                                path.Append('.').Append(property.Name);
                                CollectArrayStats(property.Value, path, depth + 1);
                                path.Length = saved;
                            }

                            break;

                        case JsonValueKind.Array:
                            CollectArrayStats(element, path, depth + 1);
                            break;
                    }
                }

                break;
            }
        }
    }

    /// <summary>Counts the object-key segments in a JSONPath: <c>$</c> is 0, <c>$.items</c> is 1, <c>$.data.results</c> is 2.</summary>
    private static int SegmentDepth(string path)
    {
        var count = 0;
        foreach (var ch in path)
        {
            if (ch == '.')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when the first sample is an object whose only non-scalar top-level field is the depth-1 array at
    /// <paramref name="anchorPath"/>: the envelope signature of scalar metadata (counts, flags, paging cursors)
    /// wrapped around one array of records. Justifies descending into a single-element result array.
    /// </summary>
    private bool IsScalarEnvelope(string anchorPath)
    {
        if (!anchorPath.StartsWith("$.", StringComparison.Ordinal))
        {
            return false;
        }

        var key = anchorPath[2..];
        if (key.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        // Every non-scalar top-level key of the first sample must be the anchor key itself.
        foreach (var nonScalarKey in _firstNonScalarTopLevelKeys)
        {
            if (!string.Equals(nonScalarKey, key, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountProperties(JsonElement obj)
    {
        var count = 0;
        foreach (var _ in obj.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// A stable structural fingerprint of a value: <c>n</c>/<c>b</c>/<c>#</c>/<c>s</c> for the scalar kinds,
    /// a sorted deduplicated element-shape list for arrays, and a key-sorted field map for objects. Two values
    /// with the same signature are the same shape, which is how homogeneity is measured and how the descent
    /// visits one representative per distinct element shape.
    /// </summary>
    private static string ShapeSignature(JsonElement value)
    {
        var builder = new StringBuilder();
        AppendShape(value, builder);
        return builder.ToString();
    }

    private static void AppendShape(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                builder.Append('n');
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                builder.Append('b');
                break;
            case JsonValueKind.Number:
                builder.Append('#');
                break;
            case JsonValueKind.String:
                builder.Append('s');
                break;
            case JsonValueKind.Array:
            {
                var shapes = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var element in value.EnumerateArray())
                {
                    var inner = new StringBuilder();
                    AppendShape(element, inner);
                    shapes.Add(inner.ToString());
                }

                builder.Append('[');
                foreach (var shape in shapes)
                {
                    builder.Append(shape).Append(',');
                }

                builder.Append(']');
                break;
            }

            case JsonValueKind.Object:
            {
                var entries = new List<JsonProperty>();
                foreach (var property in value.EnumerateObject())
                {
                    entries.Add(property);
                }

                entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                builder.Append('{');
                foreach (var property in entries)
                {
                    builder.Append(property.Name.Length).Append(':').Append(property.Name).Append('=');
                    AppendShape(property.Value, builder);
                    builder.Append(';');
                }

                builder.Append('}');
                break;
            }
        }
    }
}
