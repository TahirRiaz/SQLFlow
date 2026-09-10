using System.Collections;
using System.Data;
using System.Data.Common;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// A pass-through <see cref="DbDataReader"/> that drops every row whose watermark column is not strictly past
/// the incremental bound, so a re-run loads only new rows. It is reader-agnostic: wrapping the reader the
/// engine already streams to the bulk loader gives row-level incremental to every source kind (Parquet, CSV,
/// JSON, DuckDB, relational) from one place. DuckDB additionally pushes the same predicate into its scan, so
/// this decorator then passes those rows through unchanged; the bound is identical, so the two never disagree.
/// The watermark column ordinal is resolved once, lazily, from the inner reader's own schema.
/// </summary>
internal sealed class WatermarkFilteringDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly string _column;
    private readonly string _canonicalValue;
    private readonly WatermarkKind _kind;
    private int _ordinal = -1;

    public WatermarkFilteringDataReader(DbDataReader inner, string column, string canonicalValue, WatermarkKind kind)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(canonicalValue);
        _inner = inner;
        _column = column;
        _canonicalValue = canonicalValue;
        _kind = kind;
    }

    private int Ordinal()
    {
        if (_ordinal >= 0)
        {
            return _ordinal;
        }

        for (var i = 0; i < _inner.FieldCount; i++)
        {
            if (string.Equals(_inner.GetName(i), _column, StringComparison.OrdinalIgnoreCase))
            {
                return _ordinal = i;
            }
        }

        throw new SqlFlowException(
            $"Incremental watermark column '{_column}' is not in the source result; available: {string.Join(", ", Enumerable.Range(0, _inner.FieldCount).Select(_inner.GetName))}.");
    }

    private bool RowIsAfterBound() => WatermarkPredicate.IsAfter(_inner.GetValue(Ordinal()), _canonicalValue, _kind);

    public override bool Read()
    {
        while (_inner.Read())
        {
            if (RowIsAfterBound())
            {
                return true;
            }
        }

        return false;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _inner.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (RowIsAfterBound())
            {
                return true;
            }
        }

        return false;
    }

    public override int Depth => _inner.Depth;
    public override int FieldCount => _inner.FieldCount;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int RecordsAffected => _inner.RecordsAffected;
    public override object this[int ordinal] => _inner[ordinal];
    public override object this[string name] => _inner[name];

    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);
    public override string GetName(int ordinal) => _inner.GetName(ordinal);
    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);
    public override string GetString(int ordinal) => _inner.GetString(ordinal);
    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);
    public override int GetValues(object[] values) => _inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);
    public override bool NextResult() => _inner.NextResult();
    public override IEnumerator GetEnumerator() => _inner.GetEnumerator();

    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => _inner.IsDBNullAsync(ordinal, cancellationToken);
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);
    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();
    public override Stream GetStream(int ordinal) => _inner.GetStream(ordinal);
    public override TextReader GetTextReader(int ordinal) => _inner.GetTextReader(ordinal);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
