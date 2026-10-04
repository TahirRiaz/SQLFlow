using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

// The footer metadata types are taken by alias: importing Parquet.Meta whole shadows System.Type.
using ConvertedType = Parquet.Meta.ConvertedType;
using LogicalType = Parquet.Meta.LogicalType;
using PhysicalType = Parquet.Meta.Type;

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
            SettledFilesOnly = source.Options.GetBool(SourceOptions.SettledFilesOnly, fallback: false),
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
        await using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);

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
        await using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);

        var plan = BuildPlan(reader, file, meta);

        // Row-level incremental pushdown: when the engine injected a watermark bound, skip any whole row group
        // whose recorded MAX for the watermark column is at or below it (the file's own per-row-group statistics),
        // so a large Parquet file is not fully scanned. The engine's row-level filter still runs on the groups we
        // do read, dropping any straggler rows inside a kept group, so correctness never depends on the statistics
        // being present: a file without stats simply reads every group and is filtered row by row.
        var prune = ResolveWatermarkPrune(source, plan, reader.Metadata?.CreatedBy);
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
        if (column.Kind == ColumnKind.Scalar)
        {
            // A flat leaf has one level slot per row: a defined slot takes the next dense value, any other slot is
            // NULL (a null value, or a null ancestor struct). A required leaf has no definition levels at all.
            var leaf = await ReadLeafAsync(rowGroup, column.Leaves[0], file, ct).ConfigureAwait(false);
            var rows = checked((int)rowCount);
            if (leaf.Definitions.Length != 0 ? leaf.Definitions.Length != rows : leaf.Values.Length != rows)
            {
                throw new SqlFlowException(
                    $"Parquet file '{file.Name}' column '{leaf.Field.Path}' holds a different number of values than its row group's {rows} row(s).");
            }

            var result = new object?[rows];
            var cursor = 0;
            for (var r = 0; r < rows; r++)
            {
                var defined = leaf.Definitions.Length == 0 || leaf.Definitions[r] == leaf.Field.MaxDefinitionLevel;
                result[r] = defined ? ConvertValue(leaf.Values[cursor++]) : null;
            }

            return result;
        }

        // A nested collection: read each leaf, then reassemble one JSON document per record.
        var leaves = new LeafData[column.Leaves.Count];
        for (var i = 0; i < column.Leaves.Count; i++)
        {
            leaves[i] = await ReadLeafAsync(rowGroup, column.Leaves[i], file, ct).ConfigureAwait(false);
        }

        // Sibling leaves of one collection share its level structure; reassembly walks them in lockstep.
        var slots = leaves[0].Repetitions.Length;
        if (leaves.Any(l => l.Repetitions.Length != slots || (l.Definitions.Length != 0 && l.Definitions.Length != slots)))
        {
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' column '{column.Name}' has nested leaves with mismatched level counts and cannot be reassembled.");
        }

        return ReconstructJson(column, leaves, rowCount);
    }

    /// <summary>
    /// One leaf column of a row group: its DENSE values (one per slot whose definition level reaches the leaf's
    /// maximum, i.e. one per actual value, already in the reader's value shape), plus the per-slot definition and
    /// repetition levels (empty when the schema gives the leaf none: a required leaf has no definition levels, a
    /// non-repeated one no repetition levels).
    /// </summary>
    private sealed record LeafData(DataField Field, object?[] Values, int[] Definitions, int[] Repetitions);

    private static async Task<LeafData> ReadLeafAsync(ParquetRowGroupReader rowGroup, DataField field, FileRef file, CancellationToken ct)
    {
        RawColumnData raw;
        try
        {
            raw = await rowGroup.ReadRawColumnDataBaseAsync(field, ct).ConfigureAwait(false);
        }
        catch (OverflowException ex)
        {
            // Parquet.Net materializes a BigDecimal column into System.Decimal and overflows on a value beyond
            // its ~28-29 significant digits. Surface a clear, column-attributed error instead.
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' column '{field.Path}' has a numeric value outside the range of a "
                + ".NET/SQL Server decimal and cannot be ingested.", ex);
        }

        // The raw buffers are pooled and only valid until disposal, so levels and values are copied out first.
        using (raw)
        {
            var definitions = field.MaxDefinitionLevel > 0 ? raw.DefinitionLevels.ToArray() : [];
            var repetitions = field.MaxRepetitionLevel > 0 ? raw.RepetitionLevels.ToArray() : [];
            var count = definitions.Length == 0 ? -1 : definitions.Count(d => d == field.MaxDefinitionLevel);
            var values = DenseValues(raw, count, field, file);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = NormalizeLeafValue(field, values[i], file);
            }

            return new LeafData(field, values, definitions, repetitions);
        }
    }

    private static readonly ConcurrentDictionary<Type, Func<RawColumnData, int, DataField, FileRef, object?[]>> DenseReaders = new();

    /// <summary>
    /// Copies the first <paramref name="count"/> raw values out as objects (all of them when count is -1, a leaf
    /// with no definition levels). Text and binary values arrive as views over pooled buffers and are materialized
    /// as string / byte[] here; every other Parquet value type is boxed through one generic copier per type.
    /// </summary>
    private static object?[] DenseValues(RawColumnData raw, int count, DataField field, FileRef file)
    {
        switch (raw)
        {
            case RawColumnData<ReadOnlyMemory<char>> text:
            {
                var span = text.Values;
                var result = new object?[CheckedCount(count, span.Length, field, file)];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = span[i].ToString();
                }

                return result;
            }

            case RawColumnData<ReadOnlyMemory<byte>> binary:
            {
                var span = binary.Values;
                var result = new object?[CheckedCount(count, span.Length, field, file)];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = span[i].ToArray();
                }

                return result;
            }
        }

        var rawType = raw.GetType();
        while (rawType is not null && !(rawType.IsGenericType && rawType.GetGenericTypeDefinition() == typeof(RawColumnData<>)))
        {
            rawType = rawType.BaseType;
        }

        if (rawType is null)
        {
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' column '{field.Path}' returned column data of an unsupported shape ({raw.GetType().Name}).");
        }

        var copier = DenseReaders.GetOrAdd(rawType.GetGenericArguments()[0], static valueType =>
            typeof(ParquetSourceReader).GetMethod(nameof(BoxDense), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(valueType)
                .CreateDelegate<Func<RawColumnData, int, DataField, FileRef, object?[]>>());
        return copier(raw, count, field, file);
    }

    private static object?[] BoxDense<T>(RawColumnData raw, int count, DataField field, FileRef file)
        where T : struct
    {
        var span = ((RawColumnData<T>)raw).Values;
        var result = new object?[CheckedCount(count, span.Length, field, file)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = span[i];
        }

        return result;
    }

    private static int CheckedCount(int count, int available, DataField field, FileRef file)
    {
        if (count > available)
        {
            throw new SqlFlowException(
                $"Parquet file '{file.Name}' column '{field.Path}' declares {count} value(s) but its data holds {available}.");
        }

        return count < 0 ? available : count;
    }

    /// <summary>
    /// Restores the value shapes this reader has always produced, which Parquet.Net 6 changed: a TIME leaf comes
    /// back as its raw integer (milli/micro/nanoseconds since midnight) and becomes a <see cref="TimeSpan"/> again
    /// (landed as its "c" string, see <see cref="MapClrType"/>); an INT96 (Impala) timestamp comes back with an
    /// unspecified kind and is marked UTC again, as INT96 instants are.
    /// </summary>
    private static object? NormalizeLeafValue(DataField field, object? value, FileRef file)
    {
        switch (field)
        {
            case TimeDataField time when value is int or long:
            {
                var raw = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                try
                {
                    return TimeSpan.FromTicks(time.Precision switch
                    {
                        TimeUnitPrecision.Millis => checked(raw * TimeSpan.TicksPerMillisecond),
                        TimeUnitPrecision.Micros => checked(raw * TimeSpan.TicksPerMicrosecond),
                        TimeUnitPrecision.Nanos => raw / TimeSpan.NanosecondsPerTick,
                        _ => throw new SqlFlowException(
                            $"Parquet file '{file.Name}' column '{field.Path}' declares an unknown TIME unit '{time.Precision}'."),
                    });
                }
                catch (OverflowException ex)
                {
                    throw new SqlFlowException(
                        $"Parquet file '{file.Name}' column '{field.Path}' has a TIME value ({raw.ToString(CultureInfo.InvariantCulture)} "
                        + $"{time.Precision}) too large to represent.", ex);
                }
            }

            case DateTimeDataField { DateTimeFormat: DateTimeFormat.Impala } when value is DateTime instant:
                return DateTime.SpecifyKind(instant, DateTimeKind.Utc);

            default:
                return value;
        }
    }

    // ---- Row-level incremental pushdown -------------------------------------------------------------------

    /// <summary>
    /// The resolved watermark for row-group pruning: the scalar leaf to read statistics from, the bound, and whether
    /// the file's micro/nanosecond TIMESTAMP statistics can be trusted. Parquet.Net (6.1.0 and, conservatively, every
    /// version) writes TIMESTAMP statistics in milliseconds whatever the column's declared unit, so in its files a
    /// microsecond or nanosecond maximum understates the real one by a factor of 1000 or more and would skip groups
    /// holding new rows.
    /// </summary>
    private sealed record WatermarkPrune(DataField Leaf, string CanonicalValue, WatermarkKind Kind, bool TrustsSubMillisecondTimestamps);

    /// <summary>
    /// Resolves the engine-injected watermark to a prunable scalar leaf, or null when there is no watermark, the
    /// column is not in this file, or it is a nested column (no single MAX to prune on - the engine's row-level
    /// filter still applies). The column is matched against the cleaned plan names, exactly the names the engine's
    /// filter addresses, so the pushdown and the filter always target the same column.
    /// </summary>
    private static WatermarkPrune? ResolveWatermarkPrune(SourceSpec source, List<ColumnPlan> plan, string? createdBy)
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
                var parquetNetWriter = createdBy?.StartsWith("Parquet.Net", StringComparison.OrdinalIgnoreCase) == true;
                return new WatermarkPrune(plan[i].Leaves[0], value, kind, TrustsSubMillisecondTimestamps: !parquetNetWriter);
            }
        }

        return null;
    }

    /// <summary>
    /// True when a whole row group can be skipped: its recorded MAX for the watermark column exists and is not
    /// strictly past the bound (so every row in the group is at or below it). Pruning is best-effort and only an
    /// optimization: a missing statistic, or a max that cannot be decoded or compared as the watermark's kind,
    /// returns false so the group is read and the engine's row-level filter decides (and reports any error) row by
    /// row. Correctness never depends on the statistics existing.
    /// </summary>
    private static bool RowGroupIsAtOrBelowBound(ParquetRowGroupReader rowGroup, WatermarkPrune prune)
    {
        var max = MaxStatistic(rowGroup, prune.Leaf, prune.TrustsSubMillisecondTimestamps);
        if (max is null)
        {
            return false;
        }

        try
        {
            return !WatermarkPredicate.IsAfter(max, prune.CanonicalValue, prune.Kind);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // The maximum cannot be compared as this watermark kind (a DateTime bound over an integer column, say).
            // Skipping the group on a guess could lose rows; reading it hands the decision to the row-level filter.
            return false;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The row group's recorded maximum for a scalar leaf, decoded into exactly the value this reader yields for
    /// the column's cells, or null when there is no maximum that is certain to order like those cells (the group is
    /// then read). Only the spec's max_value is used: older writers filled the deprecated max field with
    /// inconsistent (signed byte) ordering. Never pruned: unsigned integers (their statistics are stored as signed
    /// physical values), TIME (its cells are duration text, not ordered like the stored integer), INT96, binary,
    /// UUID and other fixed-length values, and a text maximum holding supplementary characters (Parquet orders
    /// text by code point, the watermark compares UTF-16 code units, and the two disagree only there).
    /// </summary>
    private static object? MaxStatistic(ParquetRowGroupReader rowGroup, DataField field, bool trustsSubMillisecondTimestamps)
    {
        var max = rowGroup.GetMetadata(field)?.MetaData?.Statistics?.MaxValue;
        if (max is null || field.SchemaElement is not { Type: { } physical } element || field is TimeDataField)
        {
            return null;
        }

        var logical = element.LogicalType;
        var converted = element.ConvertedType;
        if (logical?.TIME is not null || converted is ConvertedType.TIME_MILLIS or ConvertedType.TIME_MICROS)
        {
            return null;
        }

        if (field is DecimalDataField || logical?.DECIMAL is not null || converted == ConvertedType.DECIMAL)
        {
            return DecodeDecimalStatistic(physical, max, element.Scale ?? logical?.DECIMAL?.Scale ?? 0);
        }

        return physical switch
        {
            PhysicalType.INT32 when max.Length == 4 => DecodeInt32Statistic(BinaryPrimitives.ReadInt32LittleEndian(max), logical, converted),
            PhysicalType.INT64 when max.Length == 8 => DecodeInt64Statistic(
                BinaryPrimitives.ReadInt64LittleEndian(max), logical, converted, trustsSubMillisecondTimestamps),
            PhysicalType.FLOAT when max.Length == 4 => BinaryPrimitives.ReadSingleLittleEndian(max) is var f && float.IsFinite(f) ? (object?)f : null,
            PhysicalType.DOUBLE when max.Length == 8 => BinaryPrimitives.ReadDoubleLittleEndian(max) is var d && double.IsFinite(d) ? (object?)d : null,
            PhysicalType.BYTE_ARRAY when LeafValueType(field) == typeof(string) => DecodeTextStatistic(max),
            _ => null,
        };
    }

    private static object? DecodeInt32Statistic(int value, LogicalType? logical, ConvertedType? converted)
    {
        if (logical?.INTEGER is { IsSigned: false } || converted is ConvertedType.UINT_8 or ConvertedType.UINT_16 or ConvertedType.UINT_32)
        {
            return null;
        }

        if (logical?.DATE is not null || converted == ConvertedType.DATE)
        {
            // Days since the Unix epoch; the reader yields a DATE as a UTC-kind midnight DateTime.
            const int MinDay = -719_162;   // 0001-01-01
            const int MaxDay = 2_932_896;  // 9999-12-31
            return value is >= MinDay and <= MaxDay ? DateTime.UnixEpoch.AddDays(value) : null;
        }

        return value;
    }

    private static object? DecodeInt64Statistic(long value, LogicalType? logical, ConvertedType? converted, bool trustsSubMillisecondTimestamps)
    {
        if (logical?.INTEGER is { IsSigned: false } || converted == ConvertedType.UINT_64)
        {
            return null;
        }

        var timestamp = logical?.TIMESTAMP;
        if (timestamp is null && converted is not (ConvertedType.TIMESTAMP_MILLIS or ConvertedType.TIMESTAMP_MICROS))
        {
            return value;
        }

        var subMillisecond = timestamp?.Unit?.MICROS is not null || timestamp?.Unit?.NANOS is not null
            || converted == ConvertedType.TIMESTAMP_MICROS;
        if (subMillisecond && !trustsSubMillisecondTimestamps)
        {
            return null;
        }

        long ticks;
        try
        {
            if (timestamp?.Unit?.NANOS is not null)
            {
                // Round up to the 100 ns tick: a maximum may overstate (a group is read), never understate.
                ticks = value / TimeSpan.NanosecondsPerTick + (value % TimeSpan.NanosecondsPerTick > 0 ? 1 : 0);
            }
            else if (timestamp?.Unit?.MICROS is not null || converted == ConvertedType.TIMESTAMP_MICROS)
            {
                ticks = checked(value * TimeSpan.TicksPerMicrosecond);
            }
            else
            {
                ticks = checked(value * TimeSpan.TicksPerMillisecond);
            }

            ticks = checked(DateTime.UnixEpoch.Ticks + ticks);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        // The reader yields a UTC-adjusted timestamp as a UTC DateTime and a local (wall-clock) one as Local, both
        // with the same ticks; the watermark comparison is by ticks.
        return new DateTime(ticks, timestamp is null || timestamp.IsAdjustedToUTC ? DateTimeKind.Utc : DateTimeKind.Local);
    }

    private static object? DecodeDecimalStatistic(PhysicalType physical, byte[] max, int scale)
    {
        BigInteger unscaled;
        switch (physical)
        {
            case PhysicalType.INT32 when max.Length == 4:
                unscaled = BinaryPrimitives.ReadInt32LittleEndian(max);
                break;
            case PhysicalType.INT64 when max.Length == 8:
                unscaled = BinaryPrimitives.ReadInt64LittleEndian(max);
                break;
            case PhysicalType.BYTE_ARRAY or PhysicalType.FIXED_LEN_BYTE_ARRAY when max.Length > 0:
                unscaled = new BigInteger(max, isUnsigned: false, isBigEndian: true);
                break;
            default:
                return null;
        }

        // System.Decimal holds a 96-bit magnitude at scale 0..28; anything wider is not prunable.
        var magnitude = BigInteger.Abs(unscaled);
        if (scale is < 0 or > 28 || magnitude >= BigInteger.One << 96)
        {
            return null;
        }

        Span<byte> bits = stackalloc byte[12];
        bits.Clear();
        magnitude.TryWriteBytes(bits, out _, isUnsigned: true, isBigEndian: false);
        return new decimal(
            BinaryPrimitives.ReadInt32LittleEndian(bits),
            BinaryPrimitives.ReadInt32LittleEndian(bits[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bits[8..]),
            unscaled.Sign < 0,
            (byte)scale);
    }

    private static string? DecodeTextStatistic(byte[] max)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(max);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8 (a truncated maximum can end mid-character): no trustworthy maximum.
            return null;
        }

        return text.Any(char.IsSurrogate) ? null : text;
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
    /// level 0; within a record each leaf value is present (consumed from its dense value array) when its
    /// definition level reaches the leaf max, otherwise it is null. An empty list yields [], a null list yields
    /// JSON null. Cross-leaf alignment is by definition level, so values never shift.
    /// </summary>
    private static object?[] ReconstructJson(ColumnPlan column, LeafData[] leaves, long rowCount)
    {
        // Repetition levels (shared across sibling leaves) segment records (level 0 = new record); per-leaf
        // definition levels say whether an element exists at a slot. Every leaf's values are DENSE (one per slot
        // whose definition level reaches the leaf max), so each leaf is consumed through its own cursor: a slot
        // below the max (an empty list, a null list, or a null element) consumes nothing, and values never shift.
        var slots = leaves[0].Repetitions;
        var defs = leaves.Select(l => l.Definitions).ToArray();
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
            // empty list (no element) or, at level 0, a null list.
            var hasElement = false;
            var elementValues = new object?[leaves.Length];
            for (var j = 0; j < leaves.Length; j++)
            {
                var threshold = maxDef[j] - (leaves[j].Field.IsNullable ? 1 : 0);
                var def = i < defs[j].Length ? defs[j][i] : maxDef[j];
                if (def >= threshold)
                {
                    // The element exists; it carries a value only at the leaf max (one level lower is a null
                    // element), and only then is the leaf's dense cursor advanced.
                    hasElement = true;
                    elementValues[j] = def == maxDef[j] ? ConvertValue(leaves[j].Values[cursor[j]++]) : null;
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

        var (type, sqlType) = MapClrType(LeafValueType(field));
        return new SourceColumn { Name = name, Type = type, SqlType = sqlType, IsNullable = true };
    }

    /// <summary>
    /// The CLR type of the values <see cref="ReadLeafAsync"/> produces for a leaf. Parquet.Net 6 declares text and
    /// binary leaves as <see cref="ReadOnlyMemory{T}"/> and a TIME leaf as its raw integer; the reader materializes
    /// those as string, byte[] and <see cref="TimeSpan"/> (see <see cref="NormalizeLeafValue"/>).
    /// </summary>
    private static Type LeafValueType(DataField field)
    {
        if (field is TimeDataField)
        {
            return typeof(TimeSpan);
        }

        var clr = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
        if (clr == typeof(ReadOnlyMemory<char>))
        {
            return typeof(string);
        }

        return clr == typeof(ReadOnlyMemory<byte>) ? typeof(byte[]) : clr;
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
