using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Translate;

namespace SqlFlow.Translate;

/// <summary>One saved file the delivery step reads back: its location, how many documents it holds, and (only
/// when the invoke URL renders per-document tokens) the document's root row.</summary>
public sealed record TranslateSavedFile(string Path, long Documents, IReadOnlyDictionary<string, object?>? Row);

/// <summary>
/// Writes rendered documents to the flow's destination in the declared layout: one file per document, one JSON
/// Lines file, or one JSON array file. Deterministic overwrite is the contract (no timestamp by default), so the
/// destination mirrors the latest translation; the single-file modes write their file even for zero documents,
/// because "the current result is empty" is itself the state the destination must reflect. Byte counts are
/// tracked at the encoder, so the reported size is exactly what was written.
/// </summary>
internal sealed class TranslateOutputWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly TranslateFlow _flow;
    private readonly IExportDestination _destination;
    private readonly bool _keepRows;
    private readonly string _singleFilePath;
    private readonly List<ExportedFile> _files = [];
    private readonly List<TranslateSavedFile> _manifest = [];
    private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);

    private Stream? _stream;
    private long _streamBytes;
    private long _documents;
    private bool _completed;

    /// <param name="flow">The flow whose output block drives layout, naming, and indentation.</param>
    /// <param name="destination">The selected write destination.</param>
    /// <param name="startUtc">The run start, for the opt-in timestamp suffix.</param>
    /// <param name="keepRows">Keep each document's root row on the manifest (only needed when the invoke URL
    /// renders per-document column tokens; rows are otherwise not retained).</param>
    public TranslateOutputWriter(TranslateFlow flow, IExportDestination destination, DateTime startUtc, bool keepRows)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(destination);
        _flow = flow;
        _destination = destination;
        _keepRows = keepRows;

        var baseName = _flow.Output.FileName is { } authored && !TranslateTemplateText.HasTokens(authored)
            ? authored
            : flow.SysAlias;
        var timestamp = flow.Output.AddTimestamp
            ? "_" + startUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            : string.Empty;
        _singleFilePath = ComposePath($"{baseName}{timestamp}");
    }

    public long Documents => _documents;

    public IReadOnlyList<ExportedFile> Files => _files;

    public IReadOnlyList<TranslateSavedFile> Manifest => _manifest;

    public async Task WriteAsync(JsonNode? document, IReadOnlyDictionary<string, object?>? row, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var index = _documents;
        _documents++;

        switch (_flow.Output.Mode)
        {
            case TranslateOutputMode.FilePerDocument:
            {
                var path = ComposePath(FileNameFor(row, index));
                if (!_usedNames.Add(path))
                {
                    throw new SqlFlowException(
                        $"output.fileName rendered the same name twice ('{path}'); a later document would silently " +
                        "overwrite an earlier one. Include a distinguishing {Column} token (a key column) in output.fileName.");
                }

                var bytes = Encoding.UTF8.GetBytes(Serialize(document, _flow.Output.Indent));
                var stream = await _destination.OpenWriteAsync(path, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }

                _files.Add(new ExportedFile(path, Rows: 1, bytes.LongLength));
                _manifest.Add(new TranslateSavedFile(path, Documents: 1, _keepRows ? row : null));
                break;
            }

            case TranslateOutputMode.JsonLines:
                await AppendAsync(Serialize(document, indent: false) + "\n", ct).ConfigureAwait(false);
                break;

            case TranslateOutputMode.Array:
            {
                var separator = index == 0 ? "[" : (_flow.Output.Indent ? ",\n" : ",");
                await AppendAsync(separator + Serialize(document, _flow.Output.Indent), ct).ConfigureAwait(false);
                break;
            }

            default:
                throw new SqlFlowException($"Unknown output mode '{_flow.Output.Mode}'.");
        }
    }

    /// <summary>Closes out the run's files. The single-file modes always produce their file, so an empty result
    /// still overwrites yesterday's file with today's (empty) truth.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_completed)
        {
            return;
        }

        if (_flow.Output.Mode is TranslateOutputMode.JsonLines or TranslateOutputMode.Array)
        {
            if (_flow.Output.Mode == TranslateOutputMode.Array)
            {
                await AppendAsync(_documents == 0 ? "[]" : "]", ct).ConfigureAwait(false);
            }
            else if (_stream is null)
            {
                // Zero documents: materialize the empty .jsonl file.
                await AppendAsync(string.Empty, ct).ConfigureAwait(false);
            }

            await _stream!.FlushAsync(ct).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            _files.Add(new ExportedFile(_singleFilePath, _documents, _streamBytes));
            _manifest.Add(new TranslateSavedFile(_singleFilePath, _documents, Row: null));
        }

        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        // Failure path: a partially written single file is closed (and left for the next run to overwrite);
        // Complete was never reached, so it is not reported as a result file.
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private async Task AppendAsync(string text, CancellationToken ct)
    {
        _stream ??= await _destination.OpenWriteAsync(_singleFilePath, ct).ConfigureAwait(false);
        if (text.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        _streamBytes += bytes.LongLength;
    }

    private static string Serialize(JsonNode? document, bool indent)
        => document?.ToJsonString(indent ? Indented : Compact) ?? "null";

    private string FileNameFor(IReadOnlyDictionary<string, object?>? row, long index)
    {
        var authored = _flow.Output.FileName;
        if (authored is null || !TranslateTemplateText.HasTokens(authored))
        {
            // No distinguishing token: the document number keeps names unique (and deterministic across runs).
            return $"{authored ?? _flow.SysAlias}_{index + 1}";
        }

        var builder = new StringBuilder();
        foreach (var segment in TranslateTemplateText.Parse(authored))
        {
            if (!segment.IsToken)
            {
                builder.Append(segment.Text);
                continue;
            }

            if (row is null || !row.TryGetValue(segment.Text, out var value))
            {
                throw new SqlFlowException(
                    $"output.fileName references column '{segment.Text}', which the primary query does not return.");
            }

            if (value is null or DBNull)
            {
                throw new SqlFlowException(
                    $"output.fileName references column '{segment.Text}', which is NULL for document {index + 1}; " +
                    "a file name must be derivable for every document.");
            }

            builder.Append(SanitizeNamePart(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return builder.ToString();
    }

    /// <summary>A rendered token value with filesystem-hostile characters replaced, so a value like an URN or a
    /// path fragment cannot escape the output folder or produce an unwritable name.</summary>
    private static string SanitizeNamePart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) || InvalidNameChars.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }

    private string ComposePath(string fileName)
    {
        var extension = _flow.Output.Mode == TranslateOutputMode.JsonLines ? ".jsonl" : ".json";
        return $"{_flow.Output.Path.TrimEnd('/', '\\')}/{fileName}{extension}";
    }
}
