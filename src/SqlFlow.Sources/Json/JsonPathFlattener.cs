using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Sources.Json;

/// <summary>
/// Flattens one JSON record into one or more ordered (column, value) rows by walking it against a
/// <see cref="JsonFlattenConfig"/>. A direct adaptation of the delta-forge recursive flattener: depth
/// guard, then exclude / json-string / scalar / object / array handling. Every value is emitted as a raw
/// string (numbers keep their original token, objects/arrays kept whole are compacted) so the downstream
/// type-inference step owns all typing.
///
/// Arrays whose path is an explode target produce one row per element, cross-producting with any other
/// exploded array in the same record. <see cref="Flatten"/> is the single-row schema view (every column
/// the record can produce, no row multiplication); <see cref="FlattenRows"/> is the full data view.
/// </summary>
public sealed class JsonPathFlattener
{
    /// <summary>
    /// Default bound on the cross-product size for one record, overridable per flow with the
    /// <c>maxRowsPerRecord</c> option. It is counted in ROWS, which is a proxy for memory and not a
    /// measure of it: a wide record costs far more per row than a narrow one.
    /// </summary>
    public const int DefaultMaxRowsPerRecord = 1_000_000;

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private readonly JsonFlattenConfig _config;

    public JsonPathFlattener(JsonFlattenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// The schema view of a record: a single row carrying every column the record can produce, including
    /// the fields of exploded arrays (visited without multiplying rows). Used to discover a file's columns.
    /// When two paths resolve to the same column name the last value wins.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Flatten(JsonElement record)
    {
        var rows = new List<OrderedRow> { new(new ColumnPlan()) };
        FlattenInto(record, "$", 0, rows, multiply: false);
        return rows[0].ToPairs();
    }

    /// <summary>
    /// The data view of a record: the actual output rows. With no explode paths this is always one row;
    /// each exploded array multiplies the rows by its element count (cross-product across sibling arrays).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> FlattenRows(JsonElement record)
    {
        var rows = new List<OrderedRow> { new(new ColumnPlan()) };
        FlattenInto(record, "$", 0, rows, multiply: true);

        var result = new List<IReadOnlyList<KeyValuePair<string, string?>>>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(row.ToPairs());
        }

        return result;
    }

    private void FlattenInto(JsonElement value, string path, int depth, List<OrderedRow> rows, bool multiply)
    {
        // Excluded subtrees are dropped entirely (no column), matching the configurator's "remove" intent.
        if (_config.IsExcluded(path))
        {
            return;
        }

        // An aliased path feeds a shared schema-evolution column; coalesce so a missing/null version-specific
        // path never nulls a value another path already supplied.
        var aliased = _config.IsAliased(path);

        // A json-string path, or anything past the depth limit, is captured whole as one JSON column.
        if (_config.IsJsonString(path) || depth > _config.MaxDepth)
        {
            SetAll(rows, _config.ColumnName(path), AsJsonString(value), aliased);
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                SetAll(rows, _config.ColumnName(path), null, aliased);
                break;

            case JsonValueKind.True:
                SetAll(rows, _config.ColumnName(path), "true", aliased);
                break;

            case JsonValueKind.False:
                SetAll(rows, _config.ColumnName(path), "false", aliased);
                break;

            case JsonValueKind.Number:
                // The raw token preserves precision and any leading/trailing significance that a parse to
                // double would lose; inference later decides int vs. decimal.
                SetAll(rows, _config.ColumnName(path), value.GetRawText(), aliased);
                break;

            case JsonValueKind.String:
                SetAll(rows, _config.ColumnName(path), value.GetString(), aliased);
                break;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
                    if (_config.IsIncluded(childPath))
                    {
                        FlattenInto(property.Value, childPath, depth + 1, rows, multiply);
                    }
                }

                break;

            case JsonValueKind.Array:
                if (_config.ShouldExplode(path))
                {
                    if (multiply)
                    {
                        ExplodeCrossProduct(value, path, depth, rows);
                    }
                    else
                    {
                        ExplodeSchema(value, path, depth, rows);
                    }
                }
                else
                {
                    HandleArray(value, path, depth, rows, multiply, aliased);
                }

                break;

            default:
                SetAll(rows, _config.ColumnName(path), AsJsonString(value), aliased);
                break;
        }
    }

    /// <summary>
    /// Schema view of an exploded array: recurse every element into the current row(s) without multiplying,
    /// so the union of element fields (even across heterogeneous elements) is captured as columns.
    /// </summary>
    private void ExplodeSchema(JsonElement array, string path, int depth, List<OrderedRow> rows)
    {
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            FlattenInto(element, $"{path}[{index}]", depth + 1, rows, multiply: false);
            index++;
        }
    }

    /// <summary>
    /// Data view of an exploded array: one output row per element. Existing rows are cloned per element and
    /// the element flattened into the clones, so several exploded arrays in one record cross-product. An
    /// empty array keeps the existing rows (left-join semantics: the parent is preserved, element columns
    /// stay null) rather than dropping the record.
    /// </summary>
    private void ExplodeCrossProduct(JsonElement array, string path, int depth, List<OrderedRow> rows)
    {
        if (array.GetArrayLength() == 0)
        {
            return;
        }

        var original = new List<OrderedRow>(rows);
        rows.Clear();

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var clones = new List<OrderedRow>(original.Count);
            foreach (var row in original)
            {
                clones.Add(row.Clone());
            }

            FlattenInto(element, $"{path}[{index}]", depth + 1, clones, multiply: true);
            rows.AddRange(clones);
            index++;

            if (rows.Count > _config.MaxRowsPerRecord)
            {
                throw new SqlFlowException(
                    $"Exploding '{path}' produced more than {_config.MaxRowsPerRecord} rows for a single record "
                    + $"(the cross product of the explode paths, not the record's size). Narrow the explode "
                    + $"paths, or raise the 'maxRowsPerRecord' option if the rows are narrow enough to fit.");
            }
        }
    }

    private void HandleArray(JsonElement array, string path, int depth, List<OrderedRow> rows, bool multiply, bool aliased)
    {
        switch (_config.ArrayHandling)
        {
            case JsonArrayHandling.FirstElement:
                if (array.GetArrayLength() > 0)
                {
                    FlattenInto(array[0], path, depth + 1, rows, multiply);
                }
                else
                {
                    SetAll(rows, _config.ColumnName(path), null, aliased);
                }

                break;

            case JsonArrayHandling.Join:
                SetAll(rows, _config.ColumnName(path), JoinScalars(array, _config.JoinSeparator), aliased);
                break;

            case JsonArrayHandling.Count:
                SetAll(rows, _config.ColumnName(path), array.GetArrayLength().ToString(CultureInfo.InvariantCulture), aliased);
                break;

            case JsonArrayHandling.Skip:
                break;

            // ToJson, and the unreachable Explode-default (an explode array never reaches here), keep the
            // array whole as one JSON-string column.
            default:
                SetAll(rows, _config.ColumnName(path), AsJsonString(array), aliased);
                break;
        }
    }

    private static void SetAll(List<OrderedRow> rows, string name, string? value, bool aliased = false)
    {
        foreach (var row in rows)
        {
            if (aliased)
            {
                row.SetCoalesce(name, value);
            }
            else
            {
                row.Set(name, value);
            }
        }
    }

    private static string JoinScalars(JsonElement array, string separator)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var element in array.EnumerateArray())
        {
            if (!first)
            {
                sb.Append(separator);
            }

            first = false;
            sb.Append(ScalarText(element));
        }

        return sb.ToString();
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => AsJsonString(element),
    };

    /// <summary>Compact JSON text for a value kept whole (excluded, json-string, or past max depth).</summary>
    private static string AsJsonString(JsonElement value) => JsonSerializer.Serialize(value, CompactJson);

    /// <summary>
    /// The per-record column plan: every column name the record has produced so far, in first-seen order,
    /// with a stable ordinal. Shared by all of a record's rows, which is what lets <see cref="OrderedRow"/>
    /// be a bare value array. Names are compared case-insensitively, matching SQL Server's default collation
    /// and the shared pipeline's column union, so two source keys differing only in case (e.g. <c>Id</c> and
    /// <c>id</c>) deterministically fold onto one column instead of being silently misassigned downstream.
    /// </summary>
    private sealed class ColumnPlan
    {
        private readonly Dictionary<string, int> _ordinalByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = [];

        public int Count => _order.Count;

        public IReadOnlyList<string> Names => _order;

        /// <summary>The ordinal of a column name, assigning the next one on first use.</summary>
        public int Ordinal(string name)
        {
            if (_ordinalByName.TryGetValue(name, out var ordinal))
            {
                return ordinal;
            }

            ordinal = _order.Count;
            _ordinalByName[name] = ordinal;
            _order.Add(name);
            return ordinal;
        }
    }

    /// <summary>
    /// One output row: nothing but its cell values, positioned by the shared <see cref="ColumnPlan"/>, with
    /// last-write-wins on a repeated column.
    ///
    /// THIS TYPE'S SIZE IS THE FLATTENER'S MEMORY PROFILE, because a cross-product explode clones it once per
    /// element of every exploded array. It previously carried its own column-name list and a case-insensitive
    /// dictionary, both deep-copied on every clone, so a wide row cost roughly an order of magnitude more
    /// than its values need. The XML flattener had the same defect and a single settlement document that
    /// explodes to a million rows took it to ~12 GB and an OutOfMemoryException (2026-08-20); see
    /// docs/flattener-memory-postmortem.md. Column names are per-record facts, so they live on the plan and a
    /// row is a bare array. Keep it that way: do not reintroduce per-row name storage.
    /// </summary>
    private sealed class OrderedRow
    {
        private readonly ColumnPlan _plan;
        private string?[] _values;

        public OrderedRow(ColumnPlan plan)
        {
            _plan = plan;
            _values = plan.Count == 0 ? [] : new string?[plan.Count];
        }

        private OrderedRow(ColumnPlan plan, string?[] values)
        {
            _plan = plan;
            _values = values;
        }

        public void Set(string name, string? value)
        {
            var ordinal = _plan.Ordinal(name);
            EnsureCapacity(ordinal);
            _values[ordinal] = value;
        }

        /// <summary>
        /// Sets a value only if it improves the column: a new column is added, and an existing null is
        /// upgraded to a non-null, but an existing non-null is never overwritten (so a later missing/null
        /// alias source path cannot blank out a value another path already supplied). An unwritten cell is
        /// already null, so that rule is exactly "write only while the cell is still null".
        /// </summary>
        public void SetCoalesce(string name, string? value)
        {
            var ordinal = _plan.Ordinal(name);
            EnsureCapacity(ordinal);
            if (_values[ordinal] is null)
            {
                _values[ordinal] = value;
            }
        }

        private void EnsureCapacity(int ordinal)
        {
            if (ordinal < _values.Length)
            {
                return;
            }

            Array.Resize(ref _values, Math.Max(_plan.Count, ordinal + 1));
        }

        public OrderedRow Clone()
        {
            var copy = new string?[_values.Length];
            Array.Copy(_values, copy, _values.Length);
            return new OrderedRow(_plan, copy);
        }

        public IReadOnlyList<KeyValuePair<string, string?>> ToPairs()
        {
            var names = _plan.Names;
            var pairs = new List<KeyValuePair<string, string?>>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                pairs.Add(new KeyValuePair<string, string?>(names[i], i < _values.Length ? _values[i] : null));
            }

            return pairs;
        }
    }
}
