using System.Collections;
using System.Data;
using System.Data.Common;

namespace SqlFlow.DuckDb;

/// <summary>
/// A pass-through <see cref="DbDataReader"/> that also owns the command and connection that produced it: when the
/// engine disposes the reader after streaming, the DuckDB command and connection are disposed too. The result is
/// never materialized; every call delegates to the inner reader, so a multi-GB scan streams row by row into the
/// bulk loader while the underlying connection stays open exactly as long as the reader is read.
/// </summary>
internal sealed class DisposingDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly DbCommand _command;
    private readonly DbConnection _connection;

    public DisposingDbDataReader(DbDataReader inner, DbCommand command, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(connection);
        _inner = inner;
        _command = command;
        _connection = connection;
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
    public override bool Read() => _inner.Read();
    public override IEnumerator GetEnumerator() => _inner.GetEnumerator();

    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => _inner.IsDBNullAsync(ordinal, cancellationToken);
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);
    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();
    public override Stream GetStream(int ordinal) => _inner.GetStream(ordinal);
    public override TextReader GetTextReader(int ordinal) => _inner.GetTextReader(ordinal);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _command.Dispose();
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await _command.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
