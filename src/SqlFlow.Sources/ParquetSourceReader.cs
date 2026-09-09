using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// Reads Apache Parquet files. Parquet is columnar and self-describing, so the file's own schema supplies the
/// column names and their real types: each scalar Parquet type maps straight to its SQL Server type and the
/// typed value streams to the bulk loader (no stringify, no inference round-trip - Parquet already knows the
/// types). A nested column (list, map, or list-of-struct) is reconstructed and kept as one JSON string column
/// (nvarchar(max)), the same "keep-a-subtree-as-a-string" fallback the JSON/XML readers use; struct fields are
/// surfaced as typed dotted columns. It rides <see cref="FileSourceReaderBase"/> for file selection, schema
/// evolution, provenance / key columns, streaming, and lifecycle - the one code path shared with CSV/XLS/JSON/XML
/// (those declare string columns, Parquet declares typed ones). Rows are materialized one row group at a time,
/// but the format needs random access (the footer lives at the end), so a remote, non-seekable file is first
/// buffered fully in memory: per file, peak memory is the file size plus one row group. A seekable (local) file
/// is not buffered and is bounded by one row group.
/// </summary>
public sealed class ParquetSourceReader : FileSourceReaderBase
{
    public ParquetSourceReader(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
        : base(lifecycle, fileStores)
    {
    }

    public override bool CanHandle(string sourceType)
        => sourceType is not null
            && (sourceType.Equals("parquet", StringComparison.OrdinalIgnoreCase)
                || sourceType.Equals("prq", StringComparison.OrdinalIgnoreCase));

    protected override string DefaultFilePattern => "*.parquet";

    protected override FileSourceOptions ReadOptions(SourceSpec source)
    {
        var meta = PreIngestionParquet.FromSource(source);
        return new FileSourceOptions
        {
            SrcPath = meta.SrcPath,
            SrcFile = meta.SrcFile,
            SrcPathMask = meta.SrcPathMask,
            SearchSubDirectories = meta.SearchSubDirectories,
            InitFromFileDate = meta.InitFromFileDate,
            InitToFileDate = meta.InitToFileDate,
            IncrementalAfterDate = meta.IncrementalAfterDate,
            FileDate = FileDateSpec.FromOptions(source.Options),
            DataSetDate = DataSetDateSpec.FromOptions(source.Options),
            ReadAhead = FileSourceOptions.ParseReadAhead(source.Options, FileSourceOptions.WholeFileDefaultReadAhead),
            CopyToPath = meta.CopyToPath,
            ZipToPath = meta.ZipToPath,
            SrcDeleteIngested = meta.SrcDeleteIngested,
            SrcDeleteAtPath = meta.SrcDeleteAtPath,
            ShowPathWithFileName = meta.ShowPathWithFileName,
            IncludeFileName = meta.IncludeFileName,
            IncludeFileDate = meta.IncludeFileDate,
            IncludeFileRowDate = meta.IncludeFileRowDate,
            IncludeFileSize = meta.IncludeFileSize,
            IncludeDataSet = meta.IncludeDataSet,
            IncludeRowNumber = meta.IncludeRowNumber,
            IncludeFileLineNumber = meta.IncludeFileLineNumber,
            IncludeHashKey = meta.IncludeHashKey,
            HashKeyColumns = meta.HashKeyColumns,
            HashKeyType = meta.HashKeyType,
            IncludeConcatKey = meta.IncludeConcatKey,
            ConcatKeyColumns = meta.ConcatKeyColumns,
            ConcatKeySeparator = meta.ConcatKeySeparator,
        };
    }

    /// <summary>The file's typed column schema: scalar/struct leaves typed, nested collections as nvarchar(max) JSON.</summary>
    protected override async Task<FileSchema> ReadFileSchemaAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var meta = PreIngestionParquet.FromSource(source);
        await using var stream = await OpenSeekableAsync(store, file, ct).ConfigureAwait(false);
        using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);

        var plan = BuildPlan(reader, file, meta);

        // Route the self-describing field names through the same legacy cleanup as every other file source,
        // index-preserving so the positional plan reader stays aligned. The cleaned names are also the raw
        // order: Parquet's row pass lays out cells from the file's own plan, not from discovered names, so the
        // JSON/XML raw-name channel just carries the file's column order.
        var columns = plan.Select(p => p.Column).ToList();
        var cleaned = CleanColumnNames(columns.Select(c => c.Name).ToList());
        return new FileSchema(columns.Select((c, i) => c with { Name = cleaned[i] }).ToList(), cleaned);
    }

    protected override async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
        => (await ReadFileSchemaAsync(store, file, source, ct).ConfigureAwait(false)).Columns.Select(c => c.Name).ToList();

    protected override async IAsyncEnumerable<FileLine> ReadLinesAsync(
        IFileStore store, FileRef file, SourceSpec source, [EnumeratorCancellation] CancellationToken ct)
    {
        var meta = PreIngestionParquet.FromSource(source);
        await using var stream = await OpenSeekableAsync(store, file, ct).ConfigureAwait(false);
        using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);

        var plan = BuildPlan(reader, file, meta);

        // Row-level incremental pushdown: when the engine injected a watermark bound, skip any whole row group
        // whose recorded MAX for the watermark column is at or below it (the file's own per-row-group statistics),
        // so a large Parquet file is not fully scanned. The engine's row-level filter still runs on the groups we
        // do read, dropping any straggler rows inside a kept group, so correctness never depends on the statistics
        // being present: a file without stats simply reads every group and is filtered row by row.
        var prune = ResolveWatermarkPrune(source, plan);
        long dataRow = 0;

        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            ct.ThrowIfCancellationRequested();

            using var rowGroup = reader.OpenRowGroupReader(group);
            var rowCount = rowGroup.RowCount;

            if (prune is not null && RowGroupIsAtOrBelowBound(rowGroup, prune))
            {
                // Advance the file-position counter past the skipped rows so kept rows keep their true line numbers.
                dataRow += rowCount;
                continue;
            }

            // Materialize each column's per-record values for this row group: scalars transpose directly; a
            // collection is reassembled from its leaf columns into one JSON string per record.
            var values = new object?[plan.Count][];
            for (var c = 0; c < plan.Count; c++)
            {
                values[c] = await MaterializeColumnAsync(plan[c], rowGroup, rowCount, file, ct).ConfigureAwait(false);
            }

            for (long r = 0; r < rowCount; r++)
            {
                var cells = new object?[plan.Count];
                for (var c = 0; c < plan.Count; c++)
                {
                    cells[c] = values[c][r];
                }

                dataRow++;
                yield return new FileLine(cells, dataRow, dataRow);
            }
        }
    }

    private static async Task<object?[]> MaterializeColumnAsync(ColumnPlan column, ParquetRowGroupReader rowGroup, long rowCount, FileRef file, CancellationToken ct)
    {
        var result = new object?[rowCount];
        if (column.Kind == ColumnKind.Scalar)
        {
            var data = (await ReadColumnAsync(rowGroup, column.Leaves[0], file, ct).ConfigureAwait(false)).Data;
            for (long r = 0; r < rowCount; r++)
            {
                result[r] = ConvertValue(data.GetValue(r));
            }

            return result;
        }

        // A nested collection: read each leaf, then reassemble one JSON document per record.
        var leaves = new DataColumn[column.Leaves.Count];
        for (var i = 0; i < column.Leaves.Count; i++)
        {
            leaves[i] = await ReadColumnAsync(rowGroup, column.Leaves[i], file, ct).ConfigureAwait(false);
        }

        return ReconstructJson(column, leaves, rowCount);
    }

    private static async Task<DataColumn> ReadColumnAsync(ParquetRowGroupReader rowGroup, DataField field, FileRef file, CancellationToken ct)
    {
        try
        {
            return await rowGroup.ReadColumnAsync(field, ct).ConfigureAwait(false);
        }
        catch (OverflowException ex)
        {
            // Parquet.Net materializes a BigDecimal column into System.Decimal and overflows on a value beyond
            // its ~28-29 significant digits. Surface a clear, column-attributed error instead.
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' column '{field.Path}' has a numeric value outside the range of a "
                + ".NET/SQL Server decimal and cannot be ingested.", ex);
        }
    }

    // ---- Row-level incremental pushdown -------------------------------------------------------------------

    /// <summary>The resolved watermark for row-group pruning: the scalar leaf to read statistics from, plus the bound.</summary>
    private sealed record WatermarkPrune(DataField Leaf, string CanonicalValue, WatermarkKind Kind);

    /// <summary>
    /// Resolves the engine-injected watermark to a prunable scalar leaf, or null when there is no watermark, the
    /// column is not in this file, or it is a nested column (no single MAX to prune on - the engine's row-level
    /// filter still applies). The column is matched against the cleaned plan names, exactly the names the engine's
    /// filter addresses, so the pushdown and the filter always target the same column.
    /// </summary>
    private static WatermarkPrune? ResolveWatermarkPrune(SourceSpec source, List<ColumnPlan> plan)
    {
        if (!source.Options.TryGetValue(WatermarkPredicate.ColumnOption, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        var value = source.Options.TryGetValue(WatermarkPredicate.ValueOption, out var v) ? v ?? string.Empty : string.Empty;
        var kindName = source.Options.TryGetValue(WatermarkPredicate.KindOption, out var k) ? k : null;
        var kind = WatermarkPredicate.ParseKind(kindName!);

        var cleaned = CleanColumnNames(plan.Select(p => p.Name).ToList());
        for (var i = 0; i < plan.Count; i++)
        {
            if (string.Equals(cleaned[i], column, StringComparison.OrdinalIgnoreCase) && plan[i].Kind == ColumnKind.Scalar)
            {
                return new WatermarkPrune(plan[i].Leaves[0], value, kind);
            }
        }

        return null;
    }

    /// <summary>
    /// True when a whole row group can be skipped: its recorded MAX for the watermark column exists and is not
    /// strictly past the bound (so every row in the group is at or below it). Pruning is best-effort and only an
    /// optimization: a missing statistic, or a max that cannot be read, returns false so the group is read and
    /// the engine's row-level filter drops the old rows. Correctness never depends on the statistics existing.
    /// </summary>
    private static bool RowGroupIsAtOrBelowBound(ParquetRowGroupReader rowGroup, WatermarkPrune prune)
    {
        object? max;
        try
        {
            max = rowGroup.GetStatistics(prune.Leaf)?.MaxValue;
        }
        catch (Exception)
        {
            return false;
        }

        return max is not null && !WatermarkPredicate.IsAfter(ConvertValue(max), prune.CanonicalValue, prune.Kind);
    }

    // ---- Schema planning ----------------------------------------------------------------------------------

    private enum ColumnKind
    {
        Scalar,
        Collection,
    }

    private enum CollectionShape
    {
        ScalarList,
        StructList,
        Map,
    }

    /// <summary>One output column: a typed scalar (one leaf) or a JSON collection (its leaf columns + shape).</summary>
    private sealed record ColumnPlan(string Name, SourceColumn Column, ColumnKind Kind, IReadOnlyList<DataField> Leaves, CollectionShape Shape, IReadOnlyList<string> ElementFieldNames);

    /// <summary>Plans the output columns from the file schema, de-colliding names. Built identically by the schema and row passes.</summary>
    private static List<ColumnPlan> BuildPlan(ParquetReader reader, FileRef file, PreIngestionParquet meta)
    {
        var leavesByField = reader.Schema.DataFields.ToList();
        var plans = new List<ColumnPlan>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in reader.Schema.Fields)
        {
            WalkField(field, string.Empty, leavesByField, plans, used, file);
        }

        // ExpectedColumnCount is the file's leaf-column count (what parquet tooling and the original engine count),
        // not the output-column count, which collapses each collection and expands each struct.
        if (meta.ExpectedColumnCount > 0 && leavesByField.Count != meta.ExpectedColumnCount)
        {
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' has {leavesByField.Count} leaf column(s) but ExpectedColumnCount is {meta.ExpectedColumnCount}.");
        }

        return plans;
    }

    private static void WalkField(Field field, string namePrefix, List<DataField> allLeaves, List<ColumnPlan> plans, HashSet<string> used, FileRef file)
    {
        switch (field)
        {
            case DataField df:
            {
                var name = DeCollide(namePrefix + df.Name, used);
                plans.Add(new ColumnPlan(name, MapColumn(name, df), ColumnKind.Scalar, [df], default, []));
                break;
            }

            case StructField sf:
            {
                // A struct flattens to typed dotted leaf columns (address.city -> address_city).
                foreach (var child in sf.Fields)
                {
                    WalkField(child, namePrefix + sf.Name + "_", allLeaves, plans, used, file);
                }

                break;
            }

            case ListField lf:
            {
                var name = DeCollide(namePrefix + lf.Name, used);
                plans.Add(BuildCollection(name, lf, allLeaves, file));
                break;
            }

            case MapField mf:
            {
                var name = DeCollide(namePrefix + mf.Name, used);
                plans.Add(BuildCollection(name, mf, allLeaves, file));
                break;
            }

            default:
                throw new SqlFlowException(
                    $"Parquet file '{file.Name}' has an unsupported schema field '{field.Name}' ({field.GetType().Name}).");
        }
    }

    /// <summary>
    /// Plans one nested column from its schema structure (not by parsing leaf-path markers): a list of scalars,
    /// a list of FLAT structs, or a map of scalar key/value. Anything deeper - a non-scalar map key/value, a
    /// struct field that is itself a struct/list/map, or a list/map of list/map - is rejected with a clear error
    /// rather than reassembled wrong.
    /// </summary>
    private static ColumnPlan BuildCollection(string name, Field field, List<DataField> allLeaves, FileRef file)
    {
        var column = new SourceColumn { Name = name, Type = typeof(string), SqlType = "nvarchar(max)", IsNullable = true };

        switch (field)
        {
            case MapField map:
            {
                if (map.Key is not DataField keyField || map.Value is not DataField valueField)
                {
                    throw new SqlFlowException(
                        $"Parquet file '{file.Name}' column '{field.Path}' is a map with a non-scalar key or value, "
                        + "which this reader does not flatten. Pre-flatten the dataset.");
                }

                var leaves = new[] { ResolveLeaf(allLeaves, keyField, field.Path.ToString(), file), ResolveLeaf(allLeaves, valueField, field.Path.ToString(), file) };
                return new ColumnPlan(name, column, ColumnKind.Collection, leaves, CollectionShape.Map, []);
            }

            case ListField { Item: DataField scalarElement } list:
            {
                var leaf = ResolveLeaf(allLeaves, scalarElement, list.Path.ToString(), file);
                return new ColumnPlan(name, column, ColumnKind.Collection, [leaf], CollectionShape.ScalarList, []);
            }

            case ListField { Item: StructField elementStruct } list:
            {
                var fieldNames = new List<string>(elementStruct.Fields.Count);
                var leaves = new List<DataField>(elementStruct.Fields.Count);
                foreach (var child in elementStruct.Fields)
                {
                    if (child is not DataField childData)
                    {
                        throw new SqlFlowException(
                            $"Parquet file '{file.Name}' column '{field.Path}' is a list of structs with a nested "
                            + $"struct/list/map field '{child.Name}', which this reader does not flatten. Pre-flatten the dataset.");
                    }

                    fieldNames.Add(child.Name);
                    leaves.Add(ResolveLeaf(allLeaves, childData, list.Path.ToString(), file));
                }

                return new ColumnPlan(name, column, ColumnKind.Collection, leaves, CollectionShape.StructList, fieldNames);
            }

            default:
                throw new SqlFlowException(
                    $"Parquet file '{file.Name}' column '{field.Path}' nests collections (a list/map of list/map), "
                    + "which this reader does not flatten. Pre-flatten the dataset.");
        }
    }

    /// <summary>Resolves a schema sub-field to its read leaf, asserting it is a single-level (repetition 1) scalar leaf.</summary>
    private static DataField ResolveLeaf(List<DataField> allLeaves, DataField field, string columnPath, FileRef file)
    {
        var path = field.Path.ToString();
        var leaf = allLeaves.FirstOrDefault(l => string.Equals(l.Path.ToString(), path, StringComparison.Ordinal))
            ?? throw new SqlFlowException($"Parquet file '{file.Name}' could not resolve nested leaf column '{path}'.");

        if (leaf.MaxRepetitionLevel != 1)
        {
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' column '{columnPath}' nests more than one level deep (repetition "
                + $"{leaf.MaxRepetitionLevel}), which this reader does not flatten. Pre-flatten the dataset.");
        }

        return leaf;
    }

    private static string DeCollide(string baseName, HashSet<string> used)
    {
        var name = baseName;
        var suffix = 2;
        while (!used.Add(name))
        {
            name = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return name;
    }

    // ---- Nested reconstruction ----------------------------------------------------------------------------

    /// <summary>
    /// Reassembles a nested column's leaves into one JSON string per record. Records are delimited by repetition
    /// level 0; within a record each leaf value is present (consumed from its dense Data array) when its
    /// definition level reaches the leaf max, otherwise it is null. An empty list yields [], a null list yields
    /// JSON null. Cross-leaf alignment is by definition level, so values never shift.
    /// </summary>
    private static object?[] ReconstructJson(ColumnPlan column, DataColumn[] leaves, long rowCount)
    {
        // Repetition levels (shared across sibling leaves) segment records (level 0 = new record); per-leaf
        // definition levels say whether an element exists at a slot. Parquet.Net 5.6.1 aligns each leaf's Data
        // array by NULLABILITY: a non-nullable leaf gets a DENSE array (one entry per existing element, indexed by
        // a cursor), while a nullable leaf gets a SLOT-ALIGNED array (one entry per repetition slot with null
        // placeholders at empty-list, null-list, and null-element slots, indexed by the slot index). Mixing the two
        // (e.g. reading a nullable leaf with a dense cursor) shifts every value after the first empty/null list.
        var slots = leaves[0].RepetitionLevels ?? [];
        var defs = leaves.Select(l => l.DefinitionLevels ?? []).ToArray();
        var maxDef = leaves.Select(l => l.Field.MaxDefinitionLevel).ToArray();
        var cursor = new int[leaves.Length];

        var result = new object?[rowCount];
        long record = -1;
        var elements = new List<object?[]>();
        var nullList = false;

        void Flush()
        {
            if (record < 0)
            {
                return;
            }

            result[record] = nullList && elements.Count == 0
                ? null
                : SerializeRecord(column, elements);
        }

        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] == 0)
            {
                Flush();
                record++;
                elements.Clear();
                nullList = false;
            }

            // An element exists at this slot unless its definition level is below the level at which the repeated
            // element is defined (maxDef for a required leaf, maxDef-1 for a nullable one). A lower level marks an
            // empty list (no element) or, at level 0, a null list. The value (already null for a null element) is
            // read slot-aligned, so cross-leaf values never shift.
            var hasElement = false;
            var elementValues = new object?[leaves.Length];
            for (var j = 0; j < leaves.Length; j++)
            {
                var threshold = maxDef[j] - (leaves[j].Field.IsNullable ? 1 : 0);
                var def = i < defs[j].Length ? defs[j][i] : maxDef[j];
                if (def >= threshold)
                {
                    // The element exists. A nullable leaf's Data is slot-aligned (read at the slot index i, which
                    // is null for a null element); a non-nullable leaf's Data is dense (read at a cursor advanced
                    // once per existing element).
                    hasElement = true;
                    var index = leaves[j].Field.IsNullable ? i : cursor[j]++;
                    elementValues[j] = ConvertValue(leaves[j].Data.GetValue(index));
                }
                else if (def == 0)
                {
                    nullList = true;
                }
            }

            if (hasElement)
            {
                elements.Add(elementValues);
                nullList = false;
            }
        }

        Flush();
        return result;
    }

    private static string SerializeRecord(ColumnPlan column, List<object?[]> elements)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            if (column.Shape == CollectionShape.Map)
            {
                w.WriteStartObject();
                foreach (var pair in elements)
                {
                    w.WritePropertyName(AsString(pair[0]) ?? string.Empty);
                    WriteJsonValue(w, pair[1]);
                }

                w.WriteEndObject();
            }
            else if (column.Shape == CollectionShape.ScalarList)
            {
                w.WriteStartArray();
                foreach (var element in elements)
                {
                    WriteJsonValue(w, element[0]);
                }

                w.WriteEndArray();
            }
            else
            {
                w.WriteStartArray();
                foreach (var element in elements)
                {
                    w.WriteStartObject();
                    for (var f = 0; f < column.ElementFieldNames.Count; f++)
                    {
                        w.WritePropertyName(column.ElementFieldNames[f]);
                        WriteJsonValue(w, element[f]);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong ul:
                w.WriteNumberValue(ul);
                break;
            case float f:
                if (float.IsFinite(f)) w.WriteNumberValue(f); else w.WriteNullValue();
                break;
            case double d:
                if (double.IsFinite(d)) w.WriteNumberValue(d); else w.WriteNullValue();
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            default:
                w.WriteStringValue(AsString(value));
                break;
        }
    }

    // ---- Scalar type mapping ------------------------------------------------------------------------------

    /// <summary>
    /// Maps a Parquet leaf field to its target SQL column: CLR value type plus explicit SQL Server type. Columns
    /// are always nullable - schema evolution across files null-fills a column missing from some of them, so a
    /// NOT NULL target would reject those rows (the original engine assumes nullable for the same reason).
    /// </summary>
    private static SourceColumn MapColumn(string name, DataField field)
    {
        if (field is DecimalDataField dec)
        {
            // SQL Server's decimal precision ceiling is 38; a Parquet decimal can declare more. Clamp to 38 -
            // values that actually fit System.Decimal (the only ones this reader can materialize) fit decimal(38,s).
            var precision = Math.Clamp(dec.Precision, 1, 38);
            var scale = Math.Clamp(dec.Scale, 0, precision);
            return new SourceColumn { Name = name, Type = typeof(decimal), SqlType = $"decimal({precision},{scale})", Precision = precision, Scale = scale, IsNullable = true };
        }

        var clr = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
        var (type, sqlType) = MapClrType(clr);
        return new SourceColumn { Name = name, Type = type, SqlType = sqlType, IsNullable = true };
    }

    private static (Type Type, string SqlType) MapClrType(Type clr)
    {
        if (clr == typeof(bool)) return (typeof(bool), "bit");
        if (clr == typeof(sbyte)) return (typeof(short), "smallint");
        if (clr == typeof(byte)) return (typeof(byte), "tinyint");
        if (clr == typeof(short)) return (typeof(short), "smallint");
        if (clr == typeof(ushort)) return (typeof(int), "int");
        if (clr == typeof(int)) return (typeof(int), "int");
        if (clr == typeof(uint)) return (typeof(long), "bigint");
        if (clr == typeof(long)) return (typeof(long), "bigint");
        if (clr == typeof(ulong)) return (typeof(decimal), "decimal(20,0)");
        if (clr == typeof(float)) return (typeof(float), "real");
        if (clr == typeof(double)) return (typeof(double), "float");
        if (clr == typeof(decimal)) return (typeof(decimal), "decimal(38,18)");
        if (clr == typeof(DateTime)) return (typeof(DateTime), "datetime2");
        if (clr == typeof(DateTimeOffset)) return (typeof(DateTimeOffset), "datetimeoffset");
        if (clr == typeof(DateOnly)) return (typeof(DateOnly), "date");
        if (clr == typeof(TimeOnly)) return (typeof(TimeOnly), "time");

        // TimeSpan is NOT mapped to SQL 'time': Parquet.Net surfaces both a time-of-day and an (unbounded,
        // possibly negative) duration as TimeSpan, and SQL 'time' only holds [0, 24h). Render it as a faithful
        // "c"-format string (CoerceCell stringifies it via AsString) so any duration survives.
        if (clr == typeof(Guid)) return (typeof(Guid), "uniqueidentifier");
        if (clr == typeof(byte[])) return (typeof(byte[]), "varbinary(max)");
        return (typeof(string), "nvarchar(max)");
    }

    /// <summary>
    /// Promotes a raw Parquet value to the CLR type the column declares: unsigned widths to the next signed type
    /// that holds them, and IEEE NaN / +/-Infinity to NULL (SQL Server's float/real cannot represent them).
    /// </summary>
    private static object? ConvertValue(object? raw) => raw switch
    {
        null => null,
        float f => float.IsFinite(f) ? f : null,
        double d => double.IsFinite(d) ? d : null,
        sbyte sb => (short)sb,
        ushort us => (int)us,
        uint ui => (long)ui,
        ulong ul => (decimal)ul,
        _ => raw,
    };

    // ---- Faithful string + stream helpers -----------------------------------------------------------------

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "True" : "False",
        float f => f.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Returns a seekable stream over the file (Parquet reads the footer at the end), buffering only if needed.</summary>
    private static async Task<Stream> OpenSeekableAsync(IFileStore store, FileRef file, CancellationToken ct)
    {
        var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        if (stream.CanSeek)
        {
            return stream;
        }

        var buffer = new MemoryStream();
        await using (stream.ConfigureAwait(false))
        {
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return buffer;
    }
}
