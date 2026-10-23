using System.Buffers;
using System.Diagnostics;
using Azure;

namespace SqlFlow.Azure.Storage;

/// <summary>Completes an operation run with <c>async: false</c>, which finishes before it returns.</summary>
internal static class Synchronously
{
    internal static void Run(ValueTask task)
    {
        Debug.Assert(task.IsCompleted, "A synchronous operation completed without awaiting.");
        task.GetAwaiter().GetResult();
    }

    internal static T Run<T>(ValueTask<T> task)
    {
        Debug.Assert(task.IsCompleted, "A synchronous operation completed without awaiting.");
        return task.GetAwaiter().GetResult();
    }
}

/// <summary>
/// A forward-only read of a Data Lake file (whole, or one range of it) that survives a dropped connection: when the body
/// fails mid-read it asks again from the byte it reached, pinned to the file's entity tag, so the bytes it returns are one
/// version of the file or a failure (status 412) if the file changed. This is what the Blob SDK's download stream does
/// over the Blob endpoint, with the same five attempts.
/// </summary>
internal sealed class DfsReadStream : Stream
{
    private const int MaxResumes = 5;

    private readonly DfsRestClient _client;
    private readonly string _fileSystem;
    private readonly string _path;
    private readonly long _end;
    private readonly string? _etag;
    private Stream _body;
    private long _position;
    private int _resumes;
    private bool _disposed;

    private DfsReadStream(DfsRestClient client, string fileSystem, string path, DfsRead first, long start, long end)
    {
        _client = client;
        _fileSystem = fileSystem;
        _path = path;
        _body = first.Content;
        _etag = first.ETag;
        _position = start;
        _end = end;
    }

    /// <summary>Opens a read of the file from <paramref name="offset"/>, of <paramref name="length"/> bytes (to the end when null).</summary>
    internal static async Task<Stream> OpenAsync(
        DfsRestClient client, string fileSystem, string path, long offset, long? length, CancellationToken ct)
    {
        var first = await client.ReadAsync(fileSystem, path, offset, length, ifMatch: null, async: true, ct).ConfigureAwait(false);
        // The exclusive end of what this read returns: the requested range, or the file's end. A body whose length the
        // service did not state can still be read, but cannot be resumed past a failure (where would it end).
        long end = length is { } count
            ? offset + (first.Length >= 0 ? first.Length : count)
            : first.Length >= 0 ? offset + first.Length : -1;
        return new DfsReadStream(client, fileSystem, path, first, offset, end);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("A Data Lake read stream is forward-only.");

    public override long Position
    {
        get => throw new NotSupportedException("A Data Lake read stream is forward-only.");
        set => throw new NotSupportedException("A Data Lake read stream is forward-only.");
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = Synchronously.Run(ReadCoreAsync(rented.AsMemory(0, buffer.Length), async: false, CancellationToken.None));
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadCoreAsync(buffer.AsMemory(offset, count), async: true, cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadCoreAsync(buffer, async: true, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, bool async, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            try
            {
                var read = async
                    ? await _body.ReadAsync(buffer, ct).ConfigureAwait(false)
                    : _body.Read(buffer.Span);
                _position += read;
                return read;
            }
            catch (IOException) when (CanResume(ct))
            {
                await ResumeAsync(async, ct).ConfigureAwait(false);
            }
        }
    }

    private bool CanResume(CancellationToken ct)
        => !ct.IsCancellationRequested && _resumes < MaxResumes && _etag is not null && _end >= 0 && _position < _end;

    private async ValueTask ResumeAsync(bool async, CancellationToken ct)
    {
        _resumes++;
        if (async)
        {
            await _body.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _body.Dispose();
        }

        var next = await _client.ReadAsync(_fileSystem, _path, _position, _end - _position, _etag, async, ct).ConfigureAwait(false);
        _body = next.Content;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("A Data Lake read stream is forward-only.");

    public override void SetLength(long value) => throw new NotSupportedException("A Data Lake read stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("A Data Lake read stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _body.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _body.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A seekable, read-only view of a Data Lake file that fetches one buffer's range at a time as it is read, pinned to the
/// entity tag the file had when it was opened: a reader that seeks (a parquet footer, then row groups) gets one version
/// of the file, or status 412 if it changes underneath. The Blob SDK's <c>OpenRead</c> with modifications disallowed,
/// over the Data Lake endpoint, with its 4 MB default buffer.
/// </summary>
internal sealed class DfsSeekableReadStream : Stream
{
    internal const int DefaultBufferSize = 4 * 1024 * 1024;
    private const int MaxAttempts = 5;

    private readonly DfsRestClient _client;
    private readonly string _fileSystem;
    private readonly string _path;
    private readonly string _etag;
    private readonly long _length;
    private readonly byte[] _buffer;
    private long _bufferStart;
    private int _bufferCount;
    private long _position;
    private bool _disposed;

    private DfsSeekableReadStream(DfsRestClient client, string fileSystem, string path, string etag, long length, int bufferSize)
    {
        _client = client;
        _fileSystem = fileSystem;
        _path = path;
        _etag = etag;
        _length = length;
        _buffer = new byte[(int)Math.Min(bufferSize, Math.Max(length, 1))];
    }

    /// <summary>Opens the file: its length and entity tag come from one properties request, and a missing file fails
    /// with status 404 here rather than at the first read.</summary>
    internal static async Task<Stream> OpenAsync(DfsRestClient client, string fileSystem, string path, int? bufferSize, CancellationToken ct)
    {
        var size = bufferSize ?? DefaultBufferSize;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size, nameof(bufferSize));
        var properties = await client.GetPathPropertiesAsync(fileSystem, path, ct).ConfigureAwait(false)
            ?? throw new RequestFailedException(404, $"The file '{path}' does not exist in '{fileSystem}'.", "PathNotFound", null);
        if (properties.IsDirectory)
        {
            throw new RequestFailedException(409, $"'{path}' in '{fileSystem}' is a directory, not a file.", "PathConflict", null);
        }

        return new DfsSeekableReadStream(client, fileSystem, path, properties.ETag ?? "*", properties.Length, size);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        if (!Buffered(_position))
        {
            Synchronously.Run(FillAsync(async: false, CancellationToken.None));
        }

        return CopyOut(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        if (!Buffered(_position))
        {
            await FillAsync(async: true, cancellationToken).ConfigureAwait(false);
        }

        return CopyOut(buffer.Span);
    }

    private bool Buffered(long position) => position >= _bufferStart && position < _bufferStart + _bufferCount;

    private int CopyOut(Span<byte> destination)
    {
        var offset = (int)(_position - _bufferStart);
        var count = Math.Min(destination.Length, _bufferCount - offset);
        _buffer.AsSpan(offset, count).CopyTo(destination);
        _position += count;
        return count;
    }

    /// <summary>Fetches the buffer's range at the current position, retrying a dropped connection mid-body.</summary>
    private async ValueTask FillAsync(bool async, CancellationToken ct)
    {
        var start = _position;
        var wanted = (int)Math.Min(_buffer.Length, _length - start);
        for (var attempt = 1; ; attempt++)
        {
            var read = await _client.ReadAsync(_fileSystem, _path, start, wanted, _etag, async, ct).ConfigureAwait(false);
            try
            {
                var filled = 0;
                using (read.Content)
                {
                    while (filled < wanted)
                    {
                        var n = async
                            ? await read.Content.ReadAsync(_buffer.AsMemory(filled, wanted - filled), ct).ConfigureAwait(false)
                            : read.Content.Read(_buffer, filled, wanted - filled);
                        if (n == 0)
                        {
                            break;
                        }

                        filled += n;
                    }
                }

                if (filled == 0)
                {
                    throw new IOException($"The Data Lake read of '{_path}' at byte {start} returned no data before the file's end ({_length} bytes).");
                }

                _bufferStart = start;
                _bufferCount = filled;
                return;
            }
            catch (IOException) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                // A connection dropped mid-body: fetch the same range again (the entity tag pins the version).
                _bufferCount = 0;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("A Data Lake read stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("A Data Lake read stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// A write to a Data Lake file that has already been created (empty): bytes are buffered and appended at their position
/// in buffer-sized requests, and a flush commits everything appended so far. <see cref="Flush"/> commits, as the Blob
/// SDK's write stream commits its blocks on flush; disposing commits what remains and closes the file, raising the
/// storage event a completed write raises. After a failed append or commit the stream commits nothing more, so disposing
/// it after an error neither throws a second, misleading error nor commits a gap.
/// </summary>
internal sealed class DfsWriteStream : Stream
{
    internal const int DefaultBufferSize = 4 * 1024 * 1024;

    private readonly DfsRestClient _client;
    private readonly string _fileSystem;
    private readonly string _path;
    private readonly byte[] _buffer;
    private int _buffered;
    private long _appended;
    private long _committed = -1;
    private bool _faulted;
    private bool _disposed;

    internal DfsWriteStream(DfsRestClient client, string fileSystem, string path, int? bufferSize)
    {
        var size = bufferSize ?? DefaultBufferSize;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size, nameof(bufferSize));
        _client = client;
        _fileSystem = fileSystem;
        _path = path;
        _buffer = new byte[size];
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException("A Data Lake write stream is forward-only.");

    public override long Position
    {
        get => _appended + _buffered;
        set => throw new NotSupportedException("A Data Lake write stream is forward-only.");
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfUnusable();
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(buffer.Length, _buffer.Length - _buffered);
            buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];
            if (_buffered == _buffer.Length)
            {
                Synchronously.Run(AppendBufferedAsync(async: false, CancellationToken.None));
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfUnusable();
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(buffer.Length, _buffer.Length - _buffered);
            buffer[..take].Span.CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];
            if (_buffered == _buffer.Length)
            {
                await AppendBufferedAsync(async: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override void Flush()
    {
        ThrowIfUnusable();
        Synchronously.Run(CommitAsync(close: false, async: false, CancellationToken.None));
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        return CommitAsync(close: false, async: true, cancellationToken).AsTask();
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new IOException($"An earlier write to the Data Lake file '{_path}' failed; the stream accepts no more data.");
        }
    }

    private async ValueTask AppendBufferedAsync(bool async, CancellationToken ct)
    {
        if (_buffered == 0)
        {
            return;
        }

        try
        {
            await _client.AppendAsync(_fileSystem, _path, _appended, _buffer.AsMemory(0, _buffered), async, ct).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _appended += _buffered;
        _buffered = 0;
    }

    private async ValueTask CommitAsync(bool close, bool async, CancellationToken ct)
    {
        await AppendBufferedAsync(async, ct).ConfigureAwait(false);
        if (_committed == _appended && !close)
        {
            return;
        }

        try
        {
            await _client.FlushAsync(_fileSystem, _path, _appended, contentHash: null, close, async, ct).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _committed = _appended;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            try
            {
                if (!_faulted)
                {
                    Synchronously.Run(CommitAsync(close: true, async: false, CancellationToken.None));
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_disposed && !_faulted)
            {
                await CommitAsync(close: true, async: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            // Marked disposed first, so the base's synchronous Dispose does not commit a second time.
            _disposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("A Data Lake write stream is write-only.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("A Data Lake write stream is forward-only.");

    public override void SetLength(long value) => throw new NotSupportedException("A Data Lake write stream is forward-only.");
}
