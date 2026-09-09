using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace SqlFlow.Core.Data;

/// <summary>
/// A forward-only <see cref="DbDataReader"/> that pulls rows from an
/// <see cref="IAsyncEnumerator{T}"/> of value arrays one at a time. Nothing is buffered, so a source
/// of any size streams straight into a consumer (e.g. SqlBulkCopy with EnableStreaming) at bounded
/// memory. A null entry in a row array is surfaced as <see cref="DBNull.Value"/>. This adapter is
/// source-agnostic: any reader can shape its rows as an async sequence and load them without
/// materialising the whole set.
/// </summary>
[SuppressMessage("Design", "CA1010:Generic interface should also be implemented",
    Justification = "DbDataReader's non-generic GetEnumerator is the established ADO.NET contract; rows are object arrays, not a homogeneous T.")]
public sealed class StreamingDataReader : DbDataReader
{
    private readonly string[] _names;
    private readonly Type[] _types;
    private readonly Dictionary<string, int> _ordinals;
    private readonly IAsyncEnumerator<object?[]> _source;
    private object?[]? _current;
    private bool _closed;
    private bool _disposed;

    public StreamingDataReader(string[] names, Type[] fieldTypes, IAsyncEnumerator<object?[]> source)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(fieldTypes);
        ArgumentNullException.ThrowIfNull(source);
        if (names.Length != fieldTypes.Length)
        {
            throw new ArgumentException("names and fieldTypes must have the same length.", nameof(fieldTypes));
        }

        _names = names;
        _types = fieldTypes;
        _source = source;
        _ordinals = new Dictionary<string, int>(names.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            _ordinals[names[i]] = i;
        }
    }

    public override int FieldCount => _names.Length;
    public override bool HasRows => true;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;
    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
        {
            return false;
        }

        if (await _source.MoveNextAsync().ConfigureAwait(false))
        {
            _current = _source.Current;
            return true;
        }

        _current = null;
        _closed = true;
        return false;
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override string GetName(int ordinal) => _names[ordinal];

    [SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
        Justification = "IDataRecord.GetOrdinal is contractually documented to throw IndexOutOfRangeException for an unknown column name.")]
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _ordinals.TryGetValue(name, out var ordinal)
            ? ordinal
            : throw new IndexOutOfRangeException($"No column named '{name}'.");
    }

    public override Type GetFieldType(int ordinal) => _types[ordinal];
    public override string GetDataTypeName(int ordinal) => _types[ordinal].Name;

    public override object GetValue(int ordinal) => Current[ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => Current[ordinal] is null;

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, _names.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override byte GetByte(int ordinal) => Convert.ToByte(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override char GetChar(int ordinal) => Convert.ToChar(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override double GetDouble(int ordinal) => Convert.ToDouble(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override float GetFloat(int ordinal) => Convert.ToSingle(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override short GetInt16(int ordinal) => Convert.ToInt16(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override int GetInt32(int ordinal) => Convert.ToInt32(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override long GetInt64(int ordinal) => Convert.ToInt64(NonNull(ordinal), CultureInfo.InvariantCulture);
    public override string GetString(int ordinal) => Convert.ToString(NonNull(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    public override Guid GetGuid(int ordinal)
        => NonNull(ordinal) is Guid guid ? guid : Guid.Parse(Convert.ToString(NonNull(ordinal), CultureInfo.InvariantCulture)!);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = Convert.ToString(NonNull(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
        if (buffer is null)
        {
            return text.Length;
        }

        if (dataOffset < 0 || dataOffset >= text.Length)
        {
            return 0;
        }

        var available = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, available);
        return available;
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = NonNull(ordinal);
        var bytes = value as byte[]
            ?? Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        if (buffer is null)
        {
            return bytes.Length;
        }

        if (dataOffset < 0 || dataOffset >= bytes.Length)
        {
            return 0;
        }

        var available = (int)Math.Min(length, bytes.Length - dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, available);
        return available;
    }

    public override DataTable GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));

        for (var i = 0; i < _names.Length; i++)
        {
            var row = schema.NewRow();
            row[SchemaTableColumn.ColumnName] = _names[i];
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.DataType] = _types[i];
            row[SchemaTableColumn.AllowDBNull] = true;
            row[SchemaTableColumn.ColumnSize] = -1;
            schema.Rows.Add(row);
        }

        return schema;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _closed = true;
            await _source.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closed = true;
        if (disposing)
        {
            _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private object?[] Current => _current ?? throw new InvalidOperationException("No row is available; call Read first.");

    private object NonNull(int ordinal)
        => Current[ordinal] ?? throw new InvalidCastException($"Column '{_names[ordinal]}' is null.");
}
