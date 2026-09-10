using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SqlFlow.Core;

namespace SqlFlow.Sources.Xml;

/// <summary>
/// Flattens one XML row element into one or more ordered (column, value) rows by walking it against an
/// <see cref="XmlFlattenConfig"/>. The XML counterpart of the JSON flattener: attributes (addressed as
/// <c>/@name</c>) and single child elements become columns, repeating sibling elements follow
/// <see cref="XmlFlattenConfig.RepeatHandling"/> (or explode into rows), excluded subtrees are dropped, and
/// xml-string paths keep a subtree as one XML-fragment column. Values are raw strings; aliased columns
/// coalesce so a missing version-specific path is never null when another supplies a value.
///
/// The flattener is the single source of truth for a record's columns: it is lossless (two distinct paths
/// that would map to the same column name are de-collided deterministically) and it reports the resolved
/// column schema (name, source path, large-text) via <see cref="SchemaColumns"/>. The de-collision is fixed
/// once per record by a schema-view pass (<see cref="NamePlan"/>) and then applied identically to every
/// exploded output row, so the schema view (which binds the load's columns and the formula) and the data
/// view (the streamed cells) always agree - a given source path lands in the same column in both.
/// </summary>
public sealed class XmlPathFlattener
{
    /// <summary>
    /// Default bound on the cross-product size for one record, overridable per flow with the
    /// <c>maxRowsPerRecord</c> option. It is counted in ROWS, which is a proxy for memory and not a
    /// measure of it: a wide record costs far more per row than a narrow one, so a workload with hundreds
    /// of columns should lower it and a narrow one may safely raise it.
    /// </summary>
    public const int DefaultMaxRowsPerRecord = 1_000_000;

    private readonly XmlFlattenConfig _config;

    public XmlPathFlattener(XmlFlattenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>The schema view of a record: one row carrying every column it can produce (no row multiplication).</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Flatten(XElement record)
        => SchemaRow(record).ToPairs();

    /// <summary>The resolved output columns of a record (name, source path, large-text), in order. Drives the formula.</summary>
    public IReadOnlyList<XmlFlattenColumn> SchemaColumns(XElement record)
        => SchemaRow(record).ToColumns();

    /// <summary>The data view of a record: the actual output rows (exploded repeating elements multiply them).</summary>
    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> FlattenRows(XElement record)
    {
        // Pass 1: the schema traversal fixes the canonical name plan (document order, all siblings folded).
        // Pass 2: the data traversal reuses that same plan, so every exploded row names columns identically to
        // the schema view that bound the load's column set.
        var plan = new NamePlan(_config.AliasTargetColumns());
        FlattenElement(record, string.Empty, 0, [new OrderedRow(plan)], multiply: false);

        var rows = new List<OrderedRow> { new(plan) };
        FlattenElement(record, string.Empty, 0, rows, multiply: true);

        var result = new List<IReadOnlyList<KeyValuePair<string, string?>>>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(row.ToPairs());
        }

        return result;
    }

    private OrderedRow SchemaRow(XElement record)
    {
        var rows = new List<OrderedRow> { new(new NamePlan(_config.AliasTargetColumns())) };
        FlattenElement(record, string.Empty, 0, rows, multiply: false);
        return rows[0];
    }

    private void FlattenElement(XElement element, string path, int depth, List<OrderedRow> rows, bool multiply)
    {
        if (_config.IncludeAttributes)
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                var attrPath = $"{path}/@{attribute.Name.LocalName}";
                if (!_config.IsExcluded(attrPath) && _config.IsIncluded(attrPath))
                {
                    SetAll(rows, attrPath, attribute.Value, largeText: false);
                }
            }
        }

        var childElements = element.Elements().ToList();

        if (childElements.Count == 0)
        {
            // A leaf element contributes its text. The row root (empty path) with no children falls back to
            // its own element name for the column, and is always emitted (a scalar record is never empty).
            var leafPath = path.Length == 0 ? "/" + element.Name.LocalName : path;
            if (path.Length == 0 || (!_config.IsExcluded(path) && _config.IsIncluded(path)))
            {
                SetAll(rows, leafPath, TextOf(element), largeText: false);
            }

            return;
        }

        // Mixed content: a container element can carry its own direct text alongside child elements. Capture
        // it (under the element's own path) so it is not silently dropped.
        var directText = DirectText(element);
        if (directText.Length > 0)
        {
            var textPath = path.Length == 0 ? "/" + element.Name.LocalName : path;
            if (path.Length == 0 || (!_config.IsExcluded(path) && _config.IsIncluded(path)))
            {
                SetAll(rows, textPath, directText, largeText: false);
            }
        }

        foreach (var group in childElements.GroupBy(e => e.Name.LocalName, StringComparer.Ordinal))
        {
            var childPath = $"{path}/{group.Key}";
            if (_config.IsExcluded(childPath) || !_config.IsIncluded(childPath))
            {
                continue;
            }

            var elements = group.ToList();

            // A kept-as-string subtree, or any subtree whose flatten would land past the depth cap, becomes one
            // XML-fragment column (large text) at this path - in both the schema and the data view.
            if (_config.IsXmlString(childPath) || depth + 1 > _config.MaxDepth)
            {
                SetAll(rows, childPath, SerializeGroup(elements), largeText: true);
                continue;
            }

            // XML cannot tell a single occurrence of a repeating element from a non-repeating one. A lone
            // element is flattened in place EXCEPT when its path is configured to explode: there it must still
            // route through explode (with a [0] subscript) so a field whose element happens to occur once in
            // some records and many times in others always lands in the same column, matching JSON arrays.
            if (elements.Count == 1 && !_config.ShouldExplode(childPath))
            {
                FlattenElement(elements[0], childPath, depth + 1, rows, multiply);
            }
            else
            {
                HandleRepeat(elements, childPath, depth, rows, multiply);
            }
        }
    }

    private void HandleRepeat(List<XElement> elements, string path, int depth, List<OrderedRow> rows, bool multiply)
    {
        if (_config.ShouldExplode(path))
        {
            if (multiply)
            {
                ExplodeCrossProduct(elements, path, depth, rows);
            }
            else
            {
                ExplodeSchema(elements, path, depth, rows);
            }

            return;
        }

        switch (_config.RepeatHandling)
        {
            case XmlRepeatHandling.FirstElement:
                FlattenElement(elements[0], path, depth + 1, rows, multiply);
                break;

            case XmlRepeatHandling.LastElement:
                FlattenElement(elements[^1], path, depth + 1, rows, multiply);
                break;

            case XmlRepeatHandling.Join:
                SetAll(rows, path, string.Join(_config.JoinSeparator, elements.Select(TextOf)), largeText: false);
                break;

            case XmlRepeatHandling.Count:
                SetAll(rows, path, elements.Count.ToString(CultureInfo.InvariantCulture), largeText: false);
                break;

            case XmlRepeatHandling.Skip:
                break;

            // ToXml, and the unreachable Explode-default (an explode path never reaches here), keep the
            // repeating elements as one XML-fragment column (large text).
            default:
                SetAll(rows, path, SerializeGroup(elements), largeText: true);
                break;
        }
    }

    /// <summary>Schema view of an exploded repeating element: fold every element into the row(s) without multiplying.</summary>
    private void ExplodeSchema(List<XElement> elements, string path, int depth, List<OrderedRow> rows)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            FlattenElement(elements[i], $"{path}[{i}]", depth + 1, rows, multiply: false);
        }
    }

    /// <summary>Data view of an exploded repeating element: one output row per element (cross-product). Empty keeps the parent.</summary>
    private void ExplodeCrossProduct(List<XElement> elements, string path, int depth, List<OrderedRow> rows)
    {
        if (elements.Count == 0)
        {
            return;
        }

        var original = new List<OrderedRow>(rows);
        rows.Clear();

        for (var i = 0; i < elements.Count; i++)
        {
            var clones = new List<OrderedRow>(original.Count);
            foreach (var row in original)
            {
                clones.Add(row.Clone());
            }

            FlattenElement(elements[i], $"{path}[{i}]", depth + 1, clones, multiply: true);
            rows.AddRange(clones);

            if (rows.Count > _config.MaxRowsPerRecord)
            {
                throw new SqlFlowException(
                    $"Exploding '{path}' produced more than {_config.MaxRowsPerRecord} rows for a single record "
                    + $"(the cross product of the explode paths, not the record's size). Narrow the explode "
                    + $"paths, or raise the 'maxRowsPerRecord' option if the rows are narrow enough to fit.");
            }
        }
    }

    /// <summary>
    /// Writes one source path's value into every row. A genuine alias source coalesces into the shared union
    /// column it feeds (never overwriting a non-null with null); any other path takes the de-collided column
    /// the shared name plan assigns to it, so the same path always lands in the same column across rows.
    /// </summary>
    private void SetAll(List<OrderedRow> rows, string rawPath, string? value, bool largeText)
    {
        var baseName = _config.ColumnName(rawPath);
        var normalizedPath = XmlFlattenConfig.NormalizeIndices(rawPath);
        var aliasSource = _config.IsAliased(rawPath);
        var normalizedValue = string.IsNullOrEmpty(value) ? null : value;

        foreach (var row in rows)
        {
            row.Write(baseName, normalizedPath, normalizedValue, largeText, aliasSource);
        }
    }

    /// <summary>Compact XML fragment for a kept subtree: the elements' raw XML, concatenated.</summary>
    private static string SerializeGroup(List<XElement> elements)
    {
        if (elements.Count == 1)
        {
            return elements[0].ToString(SaveOptions.DisableFormatting);
        }

        var sb = new StringBuilder();
        foreach (var element in elements)
        {
            sb.Append(element.ToString(SaveOptions.DisableFormatting));
        }

        return sb.ToString();
    }

    private string TextOf(XElement element) => _config.TrimText ? element.Value.Trim() : element.Value;

    /// <summary>The element's own direct text (and CDATA), excluding descendant elements' text. For mixed content.</summary>
    private string DirectText(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                sb.Append(text.Value);
            }
        }

        return _config.TrimText ? sb.ToString().Trim() : sb.ToString();
    }

    /// <summary>
    /// The per-record column plan: the assignment of a normalized source path to its final, de-collided
    /// column name, that name's stable ORDINAL, and the column's metadata. Built once during the schema
    /// traversal (document order) so the de-collision of two distinct paths that map to the same base name is
    /// identical for every exploded output row. Alias-source writes do not own a name here; they reserve their
    /// union-column name so a distinct natural field colliding on it is suffixed away instead of merged.
    ///
    /// THE PLAN HOLDS THE SCHEMA SO A ROW DOES NOT HAVE TO. Column name, source path and large-text are facts
    /// about a COLUMN, not about a row, and a record's rows all share this one instance. Keeping them here is
    /// what lets <see cref="OrderedRow"/> be a bare value array: see the note on that type for why that
    /// matters.
    /// </summary>
    private sealed class NamePlan
    {
        private readonly Dictionary<string, string> _nameByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _ordinalByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = [];
        private readonly List<ColumnMeta> _meta = [];

        /// <summary>
        /// Seeds the plan with the alias-target column names so a natural field colliding with one always
        /// de-collides into its own column, independent of whether the alias source precedes or follows it.
        /// </summary>
        public NamePlan(IEnumerable<string> reservedNames)
        {
            foreach (var name in reservedNames)
            {
                _used.Add(name);
            }
        }

        /// <summary>How many columns the plan has assigned so far.</summary>
        public int Count => _order.Count;

        /// <summary>The assigned column names, in assignment (document) order.</summary>
        public IReadOnlyList<string> Names => _order;

        /// <summary>The metadata of the column at <paramref name="ordinal"/>.</summary>
        public ColumnMeta MetaAt(int ordinal) => _meta[ordinal];

        /// <summary>
        /// The ordinal of an alias union column, assigning it on first use. The first contributing path wins
        /// the recorded metadata, matching the coalescing rule in <see cref="OrderedRow.Write"/>.
        /// </summary>
        public int ReserveAlias(string name, string normalizedPath, bool largeText)
        {
            _used.Add(name);
            return OrdinalOf(name, normalizedPath, largeText);
        }

        /// <summary>
        /// The ordinal of a natural column: its final name for this path (first assignment wins, de-collided
        /// with a suffix), then that name's position in the plan.
        /// </summary>
        public int Resolve(string normalizedPath, string baseName, bool largeText)
        {
            if (!_nameByPath.TryGetValue(normalizedPath, out var name))
            {
                name = baseName;
                var suffix = 2;
                while (!_used.Add(name))
                {
                    name = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                    suffix++;
                }

                _nameByPath[normalizedPath] = name;
            }

            return OrdinalOf(name, normalizedPath, largeText);
        }

        private int OrdinalOf(string name, string normalizedPath, bool largeText)
        {
            if (_ordinalByName.TryGetValue(name, out var ordinal))
            {
                return ordinal;
            }

            ordinal = _order.Count;
            _ordinalByName[name] = ordinal;
            _order.Add(name);
            _meta.Add(new ColumnMeta(normalizedPath, largeText));
            return ordinal;
        }
    }

    /// <summary>
    /// One output row: nothing but its cell values, positioned by the shared <see cref="NamePlan"/>.
    ///
    /// THIS TYPE'S SIZE IS THE FLATTENER'S MEMORY PROFILE, because a cross-product explode clones it once per
    /// element of every exploded repeat. It previously carried its own column-name list and TWO
    /// case-insensitive dictionaries (values and metadata), all three deep-copied on every clone, so a
    /// 96-column row cost on the order of 10 KB instead of the ~800 bytes its values actually need. A
    /// settlement document that explodes to a million rows therefore needed roughly 12 GB and died with an
    /// OutOfMemoryException (2026-08-20). The schema is per-column, so it lives on the plan and a row is a
    /// bare array. Keep it that way: do not reintroduce per-row name or metadata storage.
    /// </summary>
    private sealed class OrderedRow
    {
        private readonly NamePlan _plan;
        private string?[] _values;

        public OrderedRow(NamePlan plan)
        {
            _plan = plan;
            _values = plan.Count == 0 ? [] : new string?[plan.Count];
        }

        private OrderedRow(NamePlan plan, string?[] values)
        {
            _plan = plan;
            _values = values;
        }

        public void Write(string baseName, string normalizedPath, string? value, bool largeText, bool aliasSource)
        {
            if (aliasSource)
            {
                // Keep natural collisions off this union column, then coalesce: fill it, or upgrade a null,
                // but never overwrite a non-null. An unwritten cell is already null, so "first write wins and
                // nulls may be upgraded" is exactly "write only when the cell is still null".
                var aliasOrdinal = _plan.ReserveAlias(baseName, normalizedPath, largeText);
                EnsureCapacity(aliasOrdinal);
                if (_values[aliasOrdinal] is null)
                {
                    _values[aliasOrdinal] = value;
                }

                return;
            }

            // A natural column: first write assigns the cell, and the same path written again (each element of
            // an exploded repeat folds to one column) overwrites it.
            var ordinal = _plan.Resolve(normalizedPath, baseName, largeText);
            EnsureCapacity(ordinal);
            _values[ordinal] = value;
        }

        /// <summary>Grows to cover <paramref name="ordinal"/>. The schema pass fixes the plan before any data
        /// row exists, so in the data pass this is a no-op; it only fires while the plan is still growing.</summary>
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

        public IReadOnlyList<XmlFlattenColumn> ToColumns()
        {
            var names = _plan.Names;
            var columns = new List<XmlFlattenColumn>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                var meta = _plan.MetaAt(i);
                columns.Add(new XmlFlattenColumn(names[i], meta.SourcePath, meta.LargeText));
            }

            return columns;
        }
    }

    private readonly record struct ColumnMeta(string SourcePath, bool LargeText);
}
