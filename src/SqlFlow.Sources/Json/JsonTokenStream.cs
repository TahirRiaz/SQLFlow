using System.Buffers;
using System.Globalization;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Sources.Json;

/// <summary>
/// A forward-only, incrementally buffered view over a JSON stream: it reads tokens, skips subtrees, and
/// materializes ONE value at a time, refilling a small buffer as it goes. Nothing about a file's total size
/// enters memory - a multi-hundred-megabyte array of records costs one record plus the buffer, not the file.
/// <para>
/// <see cref="Utf8JsonReader"/> is a ref struct, so it cannot live across an await. The continuation instead
/// rides on <see cref="JsonReaderState"/>: every operation builds a reader over the buffered window from the
/// last committed state, and commits (advancing the window and saving the new state) only when the operation
/// completed inside the bytes on hand. An operation that ran out of data leaves the state untouched, the
/// buffer is refilled (and grown when a single value does not fit), and the operation is retried from exactly
/// the same position.
/// </para>
/// <para>
/// One rule governs <see cref="ParseValueAsync"/>: a restored reader knows its token type but not that token's
/// byte boundaries, so <see cref="JsonDocument.TryParseValue(ref Utf8JsonReader, out JsonDocument)"/> may only
/// be handed a reader that has read the value's first token itself. It therefore always reads that token
/// inside its own retry loop rather than relying on a caller having positioned it.
/// </para>
/// </summary>
internal sealed class JsonTokenStream : IDisposable
{
    /// <summary>
    /// The starting window. Records are typically hundreds of bytes, so this holds many of them and the buffer
    /// never grows for an ordinary file; it doubles only to fit one oversized value (a whole single-object
    /// document, say), which is the one case where the value itself must be contiguous.
    /// </summary>
    private const int InitialBufferSize = 64 * 1024;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly Stream _stream;
    private readonly string _fileName;

    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _endOfStream;
    private bool _bomChecked;
    private JsonReaderState _state = new(ReaderOptions);

    public JsonTokenStream(Stream stream, string fileName)
    {
        _stream = stream;
        _fileName = fileName;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    /// <summary>The type of the last committed token.</summary>
    public JsonTokenType TokenType { get; private set; } = JsonTokenType.None;

    /// <summary>The nesting depth of the last committed token. A container's start and matching end share it.</summary>
    public int Depth { get; private set; }

    /// <summary>1-based line of the next unread byte, tracked for parse-error messages.</summary>
    public long Line { get; private set; } = 1;

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    /// <summary>
    /// Begins a fresh top-level value. A stream can hold several (NDJSON, or concatenated documents), and each
    /// is read as its own document, so the reader state is reset rather than continued - a reader that has
    /// completed one top-level value rejects any further content.
    /// </summary>
    public void BeginDocument()
    {
        _state = new JsonReaderState(ReaderOptions);
        TokenType = JsonTokenType.None;
        Depth = 0;
    }

    /// <summary>The type of the next token without consuming it, or null at the end of the data.</summary>
    public async ValueTask<JsonTokenType?> PeekAsync(CancellationToken ct)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(Window, _endOfStream, _state);
            if (ReadToken(ref reader, Line))
            {
                return reader.TokenType;
            }

            if (_endOfStream)
            {
                return null;
            }

            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Consumes the next token. False at the end of the data.</summary>
    public async ValueTask<bool> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(Window, _endOfStream, _state);
            if (ReadToken(ref reader, Line))
            {
                Commit(ref reader);
                return true;
            }

            if (_endOfStream)
            {
                return false;
            }

            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Consumes the next token and returns its text when it is a property name, else null (the token is still
    /// consumed). Only the names walked during root-path navigation are materialized this way; skipped
    /// subtrees go through <see cref="SkipValueAsync"/>, which never allocates a name.
    /// </summary>
    public async ValueTask<string?> ReadPropertyNameAsync(CancellationToken ct)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(Window, _endOfStream, _state);
            if (ReadToken(ref reader, Line))
            {
                var name = reader.TokenType == JsonTokenType.PropertyName ? reader.GetString() : null;
                Commit(ref reader);
                return name;
            }

            if (_endOfStream)
            {
                return null;
            }

            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Consumes the next value whole - a scalar, or a container and everything inside it - without
    /// materializing any of it. A subtree is walked token by token rather than with <c>Utf8JsonReader.Skip</c>
    /// so an enormous sibling never has to be buffered contiguously just to be stepped over.
    /// </summary>
    public async ValueTask SkipValueAsync(CancellationToken ct)
    {
        if (!await ReadAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        if (TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        var depth = Depth;
        while (await ReadAsync(ct).ConfigureAwait(false))
        {
            if (TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray && Depth == depth)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Walks out of whatever containers are still open, leaving the stream positioned at the end of the current
    /// top-level value. Used after the root-path target has been consumed (or was not found) so the next
    /// top-level value, if any, starts cleanly.
    /// </summary>
    public async ValueTask SkipToEndOfDocumentAsync(CancellationToken ct)
    {
        while (Depth > 0)
        {
            if (!await ReadAsync(ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Materializes the next value into its own document, which the caller owns and disposes. Null when no
    /// value remains (end of data, or a container end where a value would have been). The returned document
    /// holds its own copy of the value's bytes, so it stays valid after the buffer moves on.
    /// </summary>
    public async ValueTask<JsonDocument?> ParseValueAsync(CancellationToken ct)
    {
        var line = Line;
        while (true)
        {
            var reader = new Utf8JsonReader(Window, _endOfStream, _state);
            JsonDocument? document = null;
            var parsed = false;

            // The reader reads the value's first token itself: TryParseValue takes the CURRENT token as the
            // value's start, and a reader restored from a saved state carries a token type without that
            // token's byte boundaries, which would slice the value from the wrong offset.
            if (ReadToken(ref reader, line))
            {
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    return null;
                }

                try
                {
                    parsed = JsonDocument.TryParseValue(ref reader, out document);
                }
                catch (JsonException ex)
                {
                    throw Invalid(ex, line);
                }
            }

            if (parsed)
            {
                Commit(ref reader);
                return document;
            }

            document?.Dispose();

            if (_endOfStream)
            {
                return null;
            }

            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Skips the whitespace and <c>#</c>-prefixed comment lines that can sit between top-level values, and
    /// reports whether any data follows. JSON has no <c>#</c> comment, so this is done over the raw bytes; it
    /// is what lets an NDJSON file carry blank and commented lines, and it is also what advances the line
    /// counter across the newline that separates two records, so a parse error names the right line.
    /// </summary>
    public async ValueTask<bool> SkipToNextDocumentAsync(CancellationToken ct)
    {
        while (true)
        {
            while (_start < _end)
            {
                var b = _buffer[_start];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    if (b == (byte)'\n')
                    {
                        Line++;
                    }

                    _start++;
                    continue;
                }

                if (b != (byte)'#')
                {
                    return true;
                }

                // A comment line: drop everything through its newline. When the newline is not buffered yet,
                // consume what is here and come back for more.
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline < 0)
                {
                    _start = _end;
                    break;
                }

                Line++;
                _start = newline + 1;
            }

            if (_endOfStream)
            {
                return false;
            }

            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    private ReadOnlySpan<byte> Window => _buffer.AsSpan(_start, _end - _start);

    /// <summary>Reads one token, translating a malformed-JSON failure into a located SqlFlow error.</summary>
    private bool ReadToken(ref Utf8JsonReader reader, long line)
    {
        try
        {
            return reader.Read();
        }
        catch (JsonException ex)
        {
            throw Invalid(ex, line);
        }
    }

    /// <summary>Accepts everything the reader consumed: advances the window, saves the state, tracks lines.</summary>
    private void Commit(ref Utf8JsonReader reader)
    {
        var consumed = (int)reader.BytesConsumed;
        Line += _buffer.AsSpan(_start, consumed).Count((byte)'\n');
        _start += consumed;
        _state = reader.CurrentState;
        TokenType = reader.TokenType;
        Depth = reader.CurrentDepth;
    }

    /// <summary>
    /// Compacts the window to the front, grows it when the pending value fills it, and reads one more chunk
    /// from the stream. Growth is what lets a single oversized value be materialized; ordinary files never
    /// trigger it because each committed record frees its bytes.
    /// </summary>
    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= Array.MaxLength / 2)
            {
                throw new SqlFlowException(
                    $"A single JSON value in '{_fileName}' exceeds {_buffer.Length.ToString(CultureInfo.InvariantCulture)} bytes, "
                    + "which is too large to materialize. Split the file, or point rootPath at the record array so the "
                    + "records are read one at a time.");
            }

            var larger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            Buffer.BlockCopy(_buffer, 0, larger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read == 0)
        {
            _endOfStream = true;
            return;
        }

        _end += read;
        StripBom();
    }

    /// <summary>Drops a leading UTF-8 BOM once the first bytes are in hand; it is not part of the JSON.</summary>
    private void StripBom()
    {
        if (_bomChecked)
        {
            return;
        }

        if (_end - _start < Utf8Bom.Length && !_endOfStream)
        {
            return;
        }

        _bomChecked = true;
        if (Window.StartsWith(Utf8Bom))
        {
            _start += Utf8Bom.Length;
        }
    }

    private SqlFlowException Invalid(JsonException inner, long line)
        => new($"Invalid JSON in '{_fileName}' at line {line.ToString(CultureInfo.InvariantCulture)}: {inner.Message}", inner);
}
