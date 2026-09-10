using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Acquire.Runtime.Protection;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The single sink every transport writes through: it enforces the empty-skip policy, renders the per-item raw path
/// from the landing template (binding <c>{page}</c>/<c>{item}</c>/<c>{runId}</c> and disambiguating collisions),
/// derives the file extension from the response format, optionally gzips, writes the raw bytes through the selected
/// <see cref="IRawLandingStore"/>, writes a redacted header sidecar when asked, and accumulates the run counters and
/// per-file manifest. The payload lands in its raw incoming format, with ONE deliberate exception: when the item
/// declares <c>landing.protect</c> rules, the <see cref="PayloadProtector"/> scrubs/pseudonymises the matched JSON
/// fields BEFORE the bytes are written, so protected data never reaches the lake. No other transform is ever applied.
/// </summary>
public sealed class LandingPipeline
{
    private readonly AcquireLanding _config;
    private readonly IRawLandingStore _store;
    private readonly string _base;
    private readonly string _runId;
    private readonly IRunEventSink _log;
    private readonly bool _dryRun;
    private readonly bool _forceReland;
    private readonly PayloadProtector? _protector;
    private readonly HashSet<string> _writtenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LandedFile> _files = [];
    // Guards the in-memory state (name reservation, counters, manifest) so an item's fan-out can land its files
    // concurrently. The store I/O itself runs outside the lock: only the fast bookkeeping is serialized.
    private readonly Lock _sync = new();

    public LandingPipeline(
        AcquireLanding config, IRawLandingStore store, string resolvedBase, Guid runId, IRunEventSink log,
        bool dryRun = false, bool forceReland = false, PayloadProtector? protector = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        _config = config;
        _store = store;
        _base = resolvedBase;
        _runId = runId.ToString("N");
        _log = log;
        _dryRun = dryRun;
        // A backfill run re-lands every re-fetched file even when byte-identical, so its timestamp is bumped and the
        // downstream incremental flows re-read it. A normal run keeps the flow's own skip-unchanged behavior.
        _forceReland = forceReland;
        if (config.Protect.Count > 0 && protector is null)
        {
            throw new ArgumentException("landing.protect rules are declared but no protector was supplied.", nameof(protector));
        }

        _protector = protector;
    }

    /// <summary>The effective unchanged-file skip: the flow's declared behavior, unless this run is an explicit
    /// reprocess (a backfill), which forces every re-fetched file to be re-written.</summary>
    private bool SkipUnchanged => _config.SkipUnchanged && !_forceReland;

    public string ResolvedBase => _base;
    public int FilesWritten { get; private set; }
    public int Skipped { get; private set; }

    /// <summary>Files that landed to a location that already held byte-identical content, so the target was left
    /// untouched (no last-modified bump, no downstream re-trigger). A subset of <see cref="FilesWritten"/>.</summary>
    public int Unchanged { get; private set; }

    public long BytesWritten { get; private set; }
    public IReadOnlyList<LandedFile> Files => _files;

    /// <summary>Lands one fetched payload, or skips it when it is empty and skip-empty is on. Returns the written
    /// file, or null when the payload was skipped. In dry-run mode the path is resolved and counted but no bytes are
    /// written (the debugger's Test invoke), so the caller sees exactly what a production Invoke would land.</summary>
    public async Task<LandedFile?> LandAsync(LandedItem item, TemplateContext vars, CancellationToken ct)
    {
        if (_config.SkipEmpty && (item.RecordCount == 0 || (item.RecordCount < 0 && item.Content.Length == 0)))
        {
            lock (_sync)
            {
                Skipped++;
            }

            _log.Log(RunLogLevel.Info, "landing.skip", $"skipped empty payload '{item.Discriminator}'.");
            return null;
        }

        var itemVars = vars.Clone()
            .WithString("page", item.Discriminator)
            .WithString("item", item.Discriminator)
            .WithString("runId", _runId);

        // Expose the response headers as {header.<name>} tokens (names lowercased) so a landing path can be keyed
        // on a per-record header, e.g. history/{header.x-entur-report-id}.xlsx. This keeps a header-keyset feed's
        // filenames stable across re-runs (same record id -> same file), which is what makes a resumed backfill
        // idempotent rather than re-landing the same payload under a fresh page index.
        if (item.Headers is { Count: > 0 })
        {
            foreach (var (headerName, headerValue) in item.Headers)
            {
                itemVars.WithString($"header.{headerName.ToLowerInvariant()}", headerValue);
            }
        }

        var relative = TemplateEngine.Render(_config.PathTemplate, itemVars).Trim('/');
        var extension = Extension(item.ContentType);
        var suffix = _config.Compression == AcquireCompression.Gzip ? ".gz" : string.Empty;

        // Reserve a collision-free name atomically: if this exact relative path was already written this run,
        // disambiguate with the item discriminator (page index / object key) before the extension. Under a
        // concurrent fan-out two lands could race for the same path, so the reservation is done under the lock.
        string name;
        lock (_sync)
        {
            name = $"{relative}.{extension}{suffix}";
            if (!_writtenPaths.Add(name))
            {
                name = $"{relative}.{Sanitize(item.Discriminator)}.{extension}{suffix}";
                _writtenPaths.Add(name);
            }
        }

        // Protection runs BEFORE compression and BEFORE any byte reaches the store, format-aware via the same
        // extension the file lands with, and a parse/unsupported-format failure throws (never "land it raw and
        // hope"): a protect-carrying flow either lands protected data or lands nothing.
        var content = _protector is not null ? _protector.Apply(item.Content, extension) : item.Content;
        var payload = _config.Compression == AcquireCompression.Gzip ? Gzip(content) : content;
        var location = _store.Combine(_base, name);
        var wrote = true;
        if (!_dryRun)
        {
            wrote = await _store.PutAsync(location, payload, _config.Overwrite, SkipUnchanged, ct).ConfigureAwait(false);
        }

        var landed = new LandedFile(location, payload.Length, item.ContentType, item.RecordCount);
        lock (_sync)
        {
            FilesWritten++;
            BytesWritten += payload.Length;
            _files.Add(landed);
            if (!_dryRun && !wrote)
            {
                Unchanged++;
            }
        }

        if (_dryRun)
        {
            _log.Log(RunLogLevel.Info, "landing.write", $"would land {payload.Length} bytes to '{location}'.");
        }
        else if (wrote)
        {
            _log.Log(RunLogLevel.Info, "landing.write", $"landed {payload.Length} bytes to '{location}'.");
        }
        else
        {
            _log.Log(RunLogLevel.Info, "landing.unchanged", $"unchanged, not rewritten: '{location}'.");
        }

        if (!_dryRun && _config.PersistHeaders && item.Headers is { Count: > 0 })
        {
            var sidecar = _store.Combine(_base, $"{relative}.headers.json");
            var json = JsonSerializer.SerializeToUtf8Bytes(HeaderRedaction.Redact(item.Headers));
            await _store.PutAsync(sidecar, json, overwrite: true, skipUnchanged: SkipUnchanged, ct: ct).ConfigureAwait(false);
        }

        return landed;
    }

    private string Extension(string? contentType)
    {
        if (!string.Equals(_config.Format, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return _config.Format.TrimStart('.').ToLowerInvariant();
        }

        var media = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return media switch
        {
            null or "" => "bin",
            "application/json" or "text/json" => "json",
            "application/xml" or "text/xml" => "xml",
            "text/csv" => "csv",
            "application/x-ndjson" or "application/jsonl" => "jsonl",
            _ when media.EndsWith("+json", StringComparison.Ordinal) => "json",
            _ when media.EndsWith("+xml", StringComparison.Ordinal) => "xml",
            _ when media.StartsWith("text/", StringComparison.Ordinal) => "txt",
            _ => "bin",
        };
    }

    private static ReadOnlyMemory<byte> Gzip(ReadOnlyMemory<byte> content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(content.Span);
        }

        return output.ToArray();
    }

    private static string Sanitize(string discriminator)
    {
        var builder = new StringBuilder(discriminator.Length);
        foreach (var c in discriminator)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return builder.Length == 0 ? "part" : builder.ToString();
    }
}
