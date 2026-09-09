using System.Data.Common;
using System.Globalization;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core.Export;

namespace SqlFlow.SqlServer.Export;

/// <summary>Writes the rows of a reader to one export file (CSV or Parquet) and returns the row count.</summary>
public interface IExportFileWriter
{
    Task<long> WriteAsync(DbDataReader reader, Stream destination, CancellationToken ct = default);
}

/// <summary>Picks the writer for a flow's <c>trgFiletype</c>.</summary>
public static class ExportFileWriterFactory
{
    public static IExportFileWriter Create(ExportFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.TrgFiletype.Trim().ToLowerInvariant() switch
        {
            "parquet" or "prq" => new ParquetExportFileWriter(flow.CompressionType),
            _ => new CsvExportFileWriter(new CsvWriteOptions
            {
                Delimiter = flow.ColumnDelimiter,
                Qualifier = string.IsNullOrEmpty(flow.TextQualifier) ? '"' : flow.TextQualifier[0],
                Encoding = ResolveEncoding(flow.TrgEncoding),
                ValueFormat = flow.TrgValueFormat,
            }),
        };
    }

    // UTF-16/UTF-32 carry their byte-order mark by definition; UTF-8 is the one that has to say so, and the
    // legacy engine's cloud path always did (new StreamWriter(stream, Encoding.UTF8)), so a port that must keep
    // its consumer parsing unchanged asks for UTF8BOM explicitly.
    private static Encoding ResolveEncoding(string? name) => (name?.Trim().ToUpperInvariant()) switch
    {
        "UNICODE" or "UTF16" or "UTF-16" => Encoding.Unicode,
        "UTF32" or "UTF-32" => Encoding.UTF32,
        "ASCII" => Encoding.ASCII,
        "UTF8BOM" or "UTF-8BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };
}

/// <summary>Settings for the CSV writer.</summary>
public sealed record CsvWriteOptions
{
    public required string Delimiter { get; init; }

    public required char Qualifier { get; init; }

    public required Encoding Encoding { get; init; }

    public bool WriteHeader { get; init; } = true;

    /// <summary>Quote every string-typed column (legacy parity); non-string columns are quoted only when their
    /// value contains the delimiter, qualifier, or a line break.</summary>
    public bool QuoteStringColumnsOnly { get; init; } = true;

    /// <summary>How non-string values are rendered as text: ISO-8601 by default, or the invariant culture's own
    /// default formats for byte parity with the legacy CsvHelper writer.</summary>
    public ExportValueFormat ValueFormat { get; init; } = ExportValueFormat.Iso;
}

/// <summary>
/// Streams a reader to a delimited (CSV) file, honoring the configured delimiter, text qualifier, and encoding
/// (legacy hardcoded these and is corrected here). A field is quoted when it is a string column or contains the
/// delimiter, the qualifier, CR, or LF; an embedded qualifier is doubled. NULL writes empty. Values are
/// formatted with the invariant culture so a locale separator never collides with the delimiter.
/// </summary>
public sealed class CsvExportFileWriter : IExportFileWriter
{
    private const string RecordSeparator = "\r\n";

    private readonly CsvWriteOptions _options;

    /// <summary>The settings this writer resolved, so a caller can confirm what a flow actually selected
    /// (the encoding's preamble and the value format decide the bytes a consumer parses).</summary>
    public CsvWriteOptions Options => _options;

    public CsvExportFileWriter(CsvWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<long> WriteAsync(DbDataReader reader, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        var fieldCount = reader.FieldCount;
        var isStringColumn = new bool[fieldCount];
        var names = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            names[i] = reader.GetName(i);
            isStringColumn[i] = reader.GetFieldType(i) == typeof(string);
        }

        await using var writer = new StreamWriter(destination, _options.Encoding, leaveOpen: true);
        if (_options.WriteHeader)
        {
            for (var i = 0; i < fieldCount; i++)
            {
                if (i > 0)
                {
                    await writer.WriteAsync(_options.Delimiter).ConfigureAwait(false);
                }

                await WriteFieldAsync(writer, names[i], isStringColumn: false).ConfigureAwait(false);
            }

            await writer.WriteAsync(RecordSeparator).ConfigureAwait(false);
        }

        long rows = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < fieldCount; i++)
            {
                if (i > 0)
                {
                    await writer.WriteAsync(_options.Delimiter).ConfigureAwait(false);
                }

                // NULL writes nothing: an empty, unquoted field, as before.
                if (!await reader.IsDBNullAsync(i, ct).ConfigureAwait(false))
                {
                    await WriteFieldAsync(writer, FormatValue(reader.GetValue(i), _options.ValueFormat), isStringColumn[i]).ConfigureAwait(false);
                }
            }

            await writer.WriteAsync(RecordSeparator).ConfigureAwait(false);
            rows++;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return rows;
    }

    // Streams one field straight into the buffered writer: quoted when required and any embedded qualifier
    // doubled in place, with no per-row field array, join, or replace copies.
    private async Task WriteFieldAsync(StreamWriter writer, string field, bool isStringColumn)
    {
        var mustQuote = (_options.QuoteStringColumnsOnly && isStringColumn)
            || field.Contains(_options.Delimiter, StringComparison.Ordinal)
            || field.Contains(_options.Qualifier)
            || field.Contains('\r')
            || field.Contains('\n');
        if (!mustQuote)
        {
            await writer.WriteAsync(field).ConfigureAwait(false);
            return;
        }

        var q = _options.Qualifier;
        await writer.WriteAsync(q).ConfigureAwait(false);
        var start = 0;
        int idx;
        while ((idx = field.IndexOf(q, start)) >= 0)
        {
            // Write up to and including the qualifier, then write it once more to double it.
            await writer.WriteAsync(field.AsMemory(start, idx - start + 1)).ConfigureAwait(false);
            await writer.WriteAsync(q).ConfigureAwait(false);
            start = idx + 1;
        }

        await writer.WriteAsync(field.AsMemory(start)).ConfigureAwait(false);
        await writer.WriteAsync(q).ConfigureAwait(false);
    }

    private static string FormatValue(object value, ExportValueFormat format) => format == ExportValueFormat.Legacy
        ? FormatLegacyValue(value)
        : FormatIsoValue(value);

    private static string FormatIsoValue(object value) => value switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double db => db.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// Renders a value the way legacy did: CsvHelper under <see cref="CultureInfo.InvariantCulture"/> called each
    /// value's own <c>ToString(IFormatProvider)</c> with no explicit format, so a DateTime came out as
    /// <c>MM/dd/yyyy HH:mm:ss</c> and a SQL <c>date</c> (read back as a midnight DateTime) as
    /// <c>MM/dd/yyyy 00:00:00</c>. Reproduced exactly so a ported export keeps its consumer parsing unchanged.
    /// The single deliberate deviation is <c>byte[]</c>: legacy rendered the literal text "System.Byte[]",
    /// discarding the value, and base64 is kept here rather than reproducing that data loss.
    /// </summary>
    private static string FormatLegacyValue(object value) => value switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        DateTime dt => dt.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString(CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double db => db.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>
/// Streams a reader to an Apache Parquet file using Parquet.Net (the columnar inverse of the reader's type
/// mapping): each reader column becomes a typed, nullable Parquet field, values accumulate directly into
/// per-column typed buffers (each cell is touched once, unboxed), and each bounded row group is written
/// column-by-column. Legacy never wrote Parquet; this is a V3 capability.
/// </summary>
public sealed class ParquetExportFileWriter : IExportFileWriter
{
    // Row-group sizing: the full 50k rows for narrow tables, scaled down on wide ones so a buffered group
    // never holds more than RowGroupCellCap cells (rows x columns). 5M cells keeps 50k rows up to 100
    // columns and bounds the in-memory footprint beyond that (e.g. 1,000 columns -> 5k rows per group).
    private const int MaxRowGroupRows = 50_000;
    private const long RowGroupCellCap = 5_000_000;

    private readonly CompressionMethod _compression;

    public ParquetExportFileWriter(string? compressionType) => _compression = ResolveCompression(compressionType);

    public async Task<long> WriteAsync(DbDataReader reader, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        var fieldCount = reader.FieldCount;
        var rowGroupRows = (int)Math.Max(1L, Math.Min(MaxRowGroupRows, RowGroupCellCap / fieldCount));
        var schemaTable = await reader.GetSchemaTableAsync(ct).ConfigureAwait(false);
        var columns = new ColumnBuffer[fieldCount];
        var fields = new Field[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            columns[i] = PlanColumn(reader.GetName(i), reader.GetFieldType(i), DecimalInfo(schemaTable, i), rowGroupRows);
            fields[i] = columns[i].Field;
        }

        using var writer = await ParquetWriter.CreateAsync(new ParquetSchema(fields), destination, cancellationToken: ct).ConfigureAwait(false);
        writer.CompressionMethod = _compression;

        long total = 0;
        var buffered = 0;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < fieldCount; i++)
            {
                if (await reader.IsDBNullAsync(i, ct).ConfigureAwait(false))
                {
                    columns[i].AppendNull();
                }
                else
                {
                    columns[i].Append(reader, i);
                }
            }

            buffered++;
            if (buffered >= rowGroupRows)
            {
                await FlushAsync(writer, columns, ct).ConfigureAwait(false);
                total += buffered;
                buffered = 0;
            }
        }

        if (buffered > 0)
        {
            await FlushAsync(writer, columns, ct).ConfigureAwait(false);
            total += buffered;
        }

        return total;
    }

    private static async Task FlushAsync(ParquetWriter writer, ColumnBuffer[] columns, CancellationToken ct)
    {
        using var rowGroup = writer.CreateRowGroup();
        foreach (var column in columns)
        {
            await rowGroup.WriteColumnAsync(new DataColumn(column.Field, column.Drain()), ct).ConfigureAwait(false);
        }
    }

    private static ColumnBuffer PlanColumn(string name, Type clr, (int Precision, int Scale)? dec, int capacity)
    {
        if (clr == typeof(bool)) return Value<bool>(name, capacity, static (r, i) => r.GetBoolean(i));
        if (clr == typeof(byte)) return Value<int>(name, capacity, static (r, i) => r.GetByte(i));
        if (clr == typeof(short)) return Value<int>(name, capacity, static (r, i) => r.GetInt16(i));
        if (clr == typeof(int)) return Value<int>(name, capacity, static (r, i) => r.GetInt32(i));
        if (clr == typeof(long)) return Value<long>(name, capacity, static (r, i) => r.GetInt64(i));
        if (clr == typeof(float)) return Value<float>(name, capacity, static (r, i) => r.GetFloat(i));
        if (clr == typeof(double)) return Value<double>(name, capacity, static (r, i) => r.GetDouble(i));
        if (clr == typeof(decimal))
        {
            return new ValueColumnBuffer<decimal>(
                new DecimalDataField(name, dec?.Precision ?? 38, dec?.Scale ?? 18, isNullable: true),
                capacity, static (r, i) => r.GetDecimal(i));
        }

        if (clr == typeof(DateTime)) return Value<DateTime>(name, capacity, static (r, i) => r.GetDateTime(i));
        if (clr == typeof(DateTimeOffset)) return Value<DateTimeOffset>(name, capacity, static (r, i) => r.GetFieldValue<DateTimeOffset>(i));
        if (clr == typeof(TimeSpan)) return Value<TimeSpan>(name, capacity, static (r, i) => r.GetFieldValue<TimeSpan>(i));
        if (clr == typeof(Guid)) return Value<Guid>(name, capacity, static (r, i) => r.GetGuid(i));
        if (clr == typeof(byte[])) return new BinaryColumnBuffer(name, capacity);
        return new StringColumnBuffer(name, capacity);
    }

    private static ColumnBuffer Value<T>(string name, int capacity, Func<DbDataReader, int, T> read) where T : struct
        => new ValueColumnBuffer<T>(new DataField<T?>(name), capacity, read);

    /// <summary>A per-column row-group buffer: each value appends once as the row streams by (no row array,
    /// no boxing for value types) and drains into the exact array shape Parquet.Net expects.</summary>
    private abstract class ColumnBuffer
    {
        protected ColumnBuffer(DataField field) => Field = field;

        public DataField Field { get; }

        public abstract void Append(DbDataReader reader, int ordinal);

        public abstract void AppendNull();

        public abstract Array Drain();
    }

    private sealed class ValueColumnBuffer<T> : ColumnBuffer where T : struct
    {
        private readonly Func<DbDataReader, int, T> _read;
        private readonly List<T?> _values;

        public ValueColumnBuffer(DataField field, int capacity, Func<DbDataReader, int, T> read)
            : base(field)
        {
            _read = read;
            _values = new List<T?>(capacity);
        }

        public override void Append(DbDataReader reader, int ordinal) => _values.Add(_read(reader, ordinal));

        public override void AppendNull() => _values.Add(null);

        public override Array Drain()
        {
            var drained = _values.ToArray();
            _values.Clear();
            return drained;
        }
    }

    private sealed class StringColumnBuffer : ColumnBuffer
    {
        private readonly List<string?> _values;

        public StringColumnBuffer(string name, int capacity)
            : base(new DataField<string>(name))
            => _values = new List<string?>(capacity);

        // Non-string CLR shapes (sql_variant and friends) land here too; anything not already a string is
        // formatted invariantly, exactly as before.
        public override void Append(DbDataReader reader, int ordinal)
        {
            var value = reader.GetValue(ordinal);
            _values.Add(value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        public override void AppendNull() => _values.Add(null);

        public override Array Drain()
        {
            var drained = _values.ToArray();
            _values.Clear();
            return drained;
        }
    }

    private sealed class BinaryColumnBuffer : ColumnBuffer
    {
        private readonly List<byte[]> _values;

        public BinaryColumnBuffer(string name, int capacity)
            : base(new DataField<byte[]>(name))
            => _values = new List<byte[]>(capacity);

        public override void Append(DbDataReader reader, int ordinal) => _values.Add(reader.GetValue(ordinal) as byte[] ?? []);

        // NULL binaries stay empty arrays, matching the previous writer's output.
        public override void AppendNull() => _values.Add([]);

        public override Array Drain()
        {
            var drained = _values.ToArray();
            _values.Clear();
            return drained;
        }
    }

    private static (int Precision, int Scale)? DecimalInfo(System.Data.DataTable? schema, int ordinal)
    {
        if (schema is null || ordinal >= schema.Rows.Count)
        {
            return null;
        }

        var row = schema.Rows[ordinal];
        if (row["NumericPrecision"] is DBNull || row["NumericScale"] is DBNull)
        {
            return null;
        }

        var precision = Math.Clamp(Convert.ToInt32(row["NumericPrecision"], CultureInfo.InvariantCulture), 1, 38);
        var scale = Math.Clamp(Convert.ToInt32(row["NumericScale"], CultureInfo.InvariantCulture), 0, precision);
        return (precision, scale);
    }

    private static CompressionMethod ResolveCompression(string? type) => (type?.Trim().ToLowerInvariant()) switch
    {
        "snappy" => CompressionMethod.Snappy,
        "none" or "" or null => CompressionMethod.None,
        _ => CompressionMethod.Gzip,
    };
}
