using System.Diagnostics;
using System.IO.Compression;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// Runs a copy flow: for each of the flow's steps it selects the source and target endpoints by their location
/// scheme, lists the matched source files, and moves them - verbatim (<c>copy</c>), bundled into one archive
/// (<c>zip</c>), or extracted from archives (<c>unzip</c>) - aggregating the matched/written counts across every
/// step so one pipeline can copy a whole source system in one run. It is direction-agnostic: any endpoint pair works,
/// because the transformation happens on the bytes between an endpoint read and an endpoint write. It never throws
/// for a transfer failure; a failed/partial <see cref="CopyRunResult"/> is returned so a batch member behaves like a
/// directly-invoked flow.
/// </summary>
public sealed class CopyEngine
{
    private static readonly IReadOnlyDictionary<string, byte[]?> EmptyIndex =
        new Dictionary<string, byte[]?>(StringComparer.Ordinal);

    private readonly IReadOnlyList<ICopyEndpoint> _endpoints;
    private readonly TimeProvider _time;

    public CopyEngine(IEnumerable<ICopyEndpoint> endpoints, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(time);
        _endpoints = endpoints.ToList();
        _time = time;
    }

    public async Task<CopyRunResult> RunAsync(
        CopyFlow flow, Guid runId, IRunEventSink log, CancellationToken ct, RunParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(log);
        var runParams = parameters ?? RunParameters.None;
        var now = _time.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var written = new List<CopyFileResult>();
        var matched = 0;
        var skipped = 0;

        try
        {
            // One flow performs many steps in order; each is an independent source-to-target transfer. The matched
            // and written counts aggregate across every step, so one pipeline that copies a whole source system
            // reports as a single run.
            var multiStep = flow.Steps.Count > 1;
            // A backfill (an operational window or a full load) is an explicit "reprocess these files" request, so the
            // unchanged-detection is disabled for the run: every selected file is re-copied and overwritten even when
            // its checksum is identical, re-landing it with a fresh timestamp so the downstream incremental flows pick
            // it up again. A normal run keeps deduplication (an idempotent re-run transfers nothing).
            var forceReland = runParams.ReprocessFiles;
            if (forceReland)
            {
                log.Log(RunLogLevel.Info, "copy.backfill",
                    "backfill run: unchanged-detection disabled, so every file in the window re-lands (overwritten even if unchanged).");
            }

            for (var i = 0; i < flow.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = flow.Steps[i];
                var source = Select(step.Source.Location, "source");
                var target = Select(step.Target.Location, "target");

                var window = ResolveWindow(step.Source, runParams, now);
                var items = new List<CopyItem>();
                await foreach (var item in source.ListAsync(step.Source, window, ct).ConfigureAwait(false))
                {
                    items.Add(item);
                }

                matched += items.Count;
                // A multi-step copy lists one folder per step, so the messages differ only by the source location -
                // and that location is a long, near-identical URL whose distinguishing tail is the first thing a
                // truncated trace view drops. Lead with the step position so each step reads as distinct at a glance.
                var stepLabel = multiStep ? $"step {i + 1}/{flow.Steps.Count}: " : string.Empty;
                log.Log(RunLogLevel.Info, "copy.list",
                    $"{stepLabel}matched {items.Count} file(s) at '{step.Source.Location}'{DescribeWindow(window)}.");

                // One bulk listing of the target's existing content hashes, so an unchanged file is detected by an
                // in-memory compare rather than a metadata round trip per file. Skipped entirely when the flow opts out
                // of unchanged-detection (options.skipUnchanged: false), so its listing/hashing cost is not paid.
                var targetIndex = flow.Options.SkipUnchanged && flow.Options.Overwrite && !forceReland
                    ? await target.TargetHashIndexAsync(step.Target, ct).ConfigureAwait(false)
                    : EmptyIndex;

                switch (flow.Operation)
                {
                    case CopyOperation.Copy:
                        skipped += await CopyAsync(flow, step, source, target, items, targetIndex, written, log, ct).ConfigureAwait(false);
                        break;
                    case CopyOperation.Zip:
                        skipped += await ZipAsync(flow, step, i, multiStep, source, target, items, targetIndex, written, log, ct).ConfigureAwait(false);
                        break;
                    case CopyOperation.Unzip:
                        skipped += await UnzipAsync(flow, step, source, target, items, targetIndex, written, log, ct).ConfigureAwait(false);
                        break;
                }
            }

            sw.Stop();
            return new CopyRunResult
            {
                RunId = runId,
                Success = true,
                DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
                Matched = matched,
                FilesWritten = written.Count,
                FilesSkipped = skipped,
                BytesWritten = written.Sum(f => f.SizeBytes),
                Files = written,
            };
        }
        // A cancellation is deliberately NOT caught here: cancelled is a distinct terminal state from failed, and it
        // is the caller that knows which one this is (an operator cancel of a queued run vs. a node shutdown). Turning
        // it into a failed result here would record an operator's cancel as a failure and strand a shutdown-interrupted
        // run that should be requeued, so it propagates to RunWorker / the CLI, which own that decision.
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is SqlFlowException or IOException or InvalidOperationException)
        {
            sw.Stop();
            var message = SecretHygiene.RedactedMessage(ex);
            log.Log(RunLogLevel.Info, "copy.error", message);
            return new CopyRunResult
            {
                RunId = runId,
                Success = false,
                Error = message,
                DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
                Matched = matched,
                FilesWritten = written.Count,
                FilesSkipped = skipped,
                BytesWritten = written.Sum(f => f.SizeBytes),
                Files = written,
            };
        }
    }

    private static async Task<int> CopyAsync(
        CopyFlow flow, CopyStep step, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        IReadOnlyDictionary<string, byte[]?> targetIndex, List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        var skipped = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var relative = flow.Options.PreserveStructure ? item.RelativePath : item.Name;

            // When the source lists a content hash (Azure blobs do), decide unchanged from the two listings alone:
            // compare it to the target's listed hash and skip WITHOUT downloading the source or writing the target.
            // This is the whole point - an idempotent re-run of a lake-to-lake copy transfers nothing for files that
            // did not move.
            if (flow.Options.Overwrite && item.ContentHash is { Length: > 0 } listedHash
                && await TargetMatchesAsync(target, step.Target, targetIndex, relative, listedHash, ct).ConfigureAwait(false))
            {
                skipped++;
                log.Log(RunLogLevel.Info, "copy.skip", $"skipped '{relative}' (no change).");
                continue;
            }

            var bytes = await source.ReadAsync(step.Source, item.AbsolutePath, ct).ConfigureAwait(false);
            var hash = item.ContentHash is { Length: > 0 } h ? h : ContentHash.Md5(bytes);

            // A source with no listed hash (local disk) is compared only after its cheap local read, so an unchanged
            // file still skips the target write (e.g. a needless re-upload to the lake).
            if (flow.Options.Overwrite && item.ContentHash is not { Length: > 0 }
                && await TargetMatchesAsync(target, step.Target, targetIndex, relative, hash, ct).ConfigureAwait(false))
            {
                skipped++;
                log.Log(RunLogLevel.Info, "copy.skip", $"skipped '{relative}' (no change).");
                continue;
            }

            var location = await target.WriteAsync(step.Target, relative, bytes, flow.Options.Overwrite, hash, ct).ConfigureAwait(false);
            written.Add(new CopyFileResult(location, bytes.Length, Hex(hash)));
            log.Log(RunLogLevel.Info, "copy.write", $"copied {bytes.Length} byte(s) -> '{location}'.");
        }

        return skipped;
    }

    /// <summary>Whether the target already holds content whose hash equals <paramref name="hash"/>, so the file can be
    /// skipped without transferring it. Resolved from the bulk <paramref name="targetIndex"/>; a listed-but-unhashed
    /// entry (local disk) is hashed on demand for just that file.</summary>
    private static async Task<bool> TargetMatchesAsync(
        ICopyEndpoint target, CopyEndpoint endpoint, IReadOnlyDictionary<string, byte[]?> targetIndex,
        string relativePath, byte[] hash, CancellationToken ct)
    {
        if (!targetIndex.TryGetValue(relativePath, out var targetHash))
        {
            return false; // the target has no such file
        }

        targetHash ??= await target.TargetContentHashAsync(endpoint, relativePath, ct).ConfigureAwait(false);
        return targetHash is { Length: > 0 } && targetHash.AsSpan().SequenceEqual(hash);
    }

    /// <summary>The lowercase-hex form of a content hash, for the run's file manifest and the catalog.</summary>
    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private async Task<int> ZipAsync(
        CopyFlow flow, CopyStep step, int stepIndex, bool multiStep, ICopyEndpoint source, ICopyEndpoint target,
        IReadOnlyList<CopyItem> items, IReadOnlyDictionary<string, byte[]?> targetIndex, List<CopyFileResult> written,
        IRunEventSink log, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            log.Log(RunLogLevel.Info, "copy.zip", $"no files matched at '{step.Source.Location}'; no archive written.");
            return 0;
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await source.ReadAsync(step.Source, item.AbsolutePath, ct).ConfigureAwait(false);
                var entryName = (flow.Options.PreserveStructure ? item.RelativePath : item.Name).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
        }

        // A single explicit ZipName names the one archive; across several steps it is per-step (the source leaf keeps
        // each step's archive distinct) so multiple steps never overwrite one bundle.
        var zipName = !multiStep && !string.IsNullOrWhiteSpace(flow.Options.ZipName)
            ? flow.Options.ZipName!
            : $"{flow.Name}_{SourceLeaf(step.Source.Location)}_{_time.GetUtcNow():yyyyMMddHHmmss}.zip";
        var payload = buffer.ToArray();
        var hash = ContentHash.Md5(payload);
        if (flow.Options.Overwrite && await TargetMatchesAsync(target, step.Target, targetIndex, zipName, hash, ct).ConfigureAwait(false))
        {
            log.Log(RunLogLevel.Info, "copy.skip", $"skipped archive '{zipName}' (no change).");
            return 1;
        }

        var location = await target.WriteAsync(step.Target, zipName, payload, flow.Options.Overwrite, hash, ct).ConfigureAwait(false);
        written.Add(new CopyFileResult(location, payload.Length, Hex(hash)));
        log.Log(RunLogLevel.Info, "copy.zip", $"zipped {items.Count} file(s) ({payload.Length} byte(s)) -> '{location}'.");
        return 0;
    }

    private static async Task<int> UnzipAsync(
        CopyFlow flow, CopyStep step, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        IReadOnlyDictionary<string, byte[]?> targetIndex, List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        var skipped = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await source.ReadAsync(step.Source, item.AbsolutePath, ct).ConfigureAwait(false);
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            // Extract each archive under a folder named for the archive (minus its extension) when preserving
            // structure, so two archives' entries never collide; flat by entry name otherwise.
            var prefix = flow.Options.PreserveStructure
                ? Path.GetFileNameWithoutExtension(item.Name) + "/"
                : string.Empty;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                await using var entryStream = entry.Open();
                using var entryBuffer = new MemoryStream();
                await entryStream.CopyToAsync(entryBuffer, ct).ConfigureAwait(false);
                var relative = prefix + (flow.Options.PreserveStructure ? entry.FullName : entry.Name);
                var payload = entryBuffer.ToArray();
                var hash = ContentHash.Md5(payload);
                if (flow.Options.Overwrite && await TargetMatchesAsync(target, step.Target, targetIndex, relative, hash, ct).ConfigureAwait(false))
                {
                    skipped++;
                    log.Log(RunLogLevel.Info, "copy.skip", $"skipped '{relative}' (no change).");
                    continue;
                }

                var location = await target.WriteAsync(step.Target, relative, payload, flow.Options.Overwrite, hash, ct).ConfigureAwait(false);
                written.Add(new CopyFileResult(location, payload.Length, Hex(hash)));
                log.Log(RunLogLevel.Info, "copy.unzip", $"extracted {payload.Length} byte(s) -> '{location}'.");
            }
        }

        return skipped;
    }

    private static string SourceLeaf(string location)
    {
        var trimmed = location.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        // Keep only path-safe characters so the archive name is always writable (an abfss authority leaf could carry
        // an '@' or ':'); fall back to the step position when nothing usable remains.
        var clean = new string(leaf.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return clean.Length > 0 ? clean : "step";
    }

    /// <summary>
    /// The effective modified-date filter for a source step: an operational backfill window (from the run's
    /// parameters) overrides the flow's declared <c>modifiedWithinDays</c>; a full-load run copies everything the
    /// definition selects (no date filter); otherwise the endpoint's <c>modifiedWithinDays</c> default applies,
    /// computed against the engine's clock. <c>filePattern</c> and <c>assertionsOnly</c> have no meaning for a
    /// byte-for-byte copy and are ignored here (the run log still records the supplied parameters).
    /// </summary>
    private static CopyModifiedWindow ResolveWindow(CopyEndpoint source, RunParameters parameters, DateTimeOffset now)
    {
        if (parameters.BackfillFrom is { } from)
        {
            var to = parameters.BackfillTo is { } t
                ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            return new CopyModifiedWindow(new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)), to);
        }

        if (parameters.FullLoad)
        {
            return CopyModifiedWindow.Unbounded;
        }

        return source.ModifiedWithinDays > 0
            ? new CopyModifiedWindow(now.AddDays(-source.ModifiedWithinDays), null)
            : CopyModifiedWindow.Unbounded;
    }

    private static string DescribeWindow(CopyModifiedWindow window)
        => window switch
        {
            { From: { } f, To: { } t } => $" (modified {f:yyyy-MM-dd} .. {t:yyyy-MM-dd})",
            { From: { } f } => $" (modified since {f:yyyy-MM-dd})",
            { To: { } t } => $" (modified until {t:yyyy-MM-dd})",
            _ => string.Empty,
        };

    private ICopyEndpoint Select(string location, string side)
        => _endpoints.FirstOrDefault(e => e.CanHandle(location))
           ?? throw new SqlFlowException(
               $"No copy endpoint handles the {side} location '{location}'. Use a local/UNC path, an Azure storage URI "
               + "(abfss://…/https://…), or an S3 URI (s3://bucket/prefix). For SFTP, use the dedicated sftp flow type.");
}
