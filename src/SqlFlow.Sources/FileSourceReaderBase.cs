using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Data;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// The shared, format-agnostic file-ingestion pipeline: file selection (glob, path-mask regex, date
/// window, incremental watermark), dynamic schema evolution (union columns across files, NULL-fill),
/// provenance column injection with correct types, synthetic hash/concatenation keys, streaming into
/// the bulk loader at bounded memory, and the post-load file lifecycle (copy/zip/delete). A concrete
/// reader supplies only the format-specific parts: how to read a file's column names and how to stream
/// its rows. This keeps every format (CSV, XLS, ...) on one code path.
/// </summary>
public abstract class FileSourceReaderBase : ISourceReader
{
    /// <summary>
    /// The file's business timestamp for <c>FileDate_DW</c>: the instant encoded in its name or path when the flow
    /// configured a <c>fileDate</c> source, else its last-modified time.
    /// <para>
    /// The fallback is deliberate and applies per file, not per flow: a spec that matches most files but not one
    /// oddly-named straggler stamps that straggler with its modified time rather than failing the run or leaving
    /// the column null. Selection has already decided whether the file belongs to this load (an undated file is
    /// excluded from a dated selection), so by the time provenance is written the only question left is what to
    /// record, and the modified time is the honest answer when the name carries nothing.
    /// </para>
    /// </summary>
    private static DateTime ResolveFileDate(FileDateSpec? spec, FileRef file, DateTime fileModifiedUtc)
    {
        if (spec is null)
        {
            return fileModifiedUtc;
        }

        var text = spec.Source == FileDateSource.Path ? file.Path : file.Name;
        return spec.ExtractTimestamp(text) ?? fileModifiedUtc;
    }

    protected const string FileLineNumberColumn = "FileLineNumber";
    protected const string FileNameColumn = "FileName_DW";
    protected const string FileDateColumn = "FileDate_DW";
    protected const string FileRowDateColumn = "FileRowDate_DW";
    protected const string FileSizeColumn = "FileSize_DW";
    protected const string DataSetColumn = "DataSet_DW";
    protected const string RowNumberColumn = "RowNumber_DW";
    protected const string HashKeyColumn = "HashKey_DW";
    protected const string ConcatKeyColumn = "ConcatKey_DW";

    private readonly IFileLifecycle _lifecycle;
    private readonly IReadOnlyList<IFileStore> _fileStores;

    // Per-run cache, keyed by the SourceSpec instance the engine threads through a run's three stages
    // (GetColumnsAsync, OpenAsync, CompleteAsync). Concurrent runs load distinct spec instances, so they never
    // share an entry; the weak table drops an entry when its spec dies, and CompleteAsync evicts eagerly so a
    // host that re-runs a cached spec instance still resolves fresh.
    private readonly ConditionalWeakTable<SourceSpec, RunState> _runs = new();

    protected FileSourceReaderBase(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
    {
        ArgumentNullException.ThrowIfNull(fileStores);
        _lifecycle = lifecycle;
        _fileStores = fileStores.ToList();
    }

    /// <summary>
    /// One run's cached file selection and per-file schemas. The snapshot semantic is deliberate: the file list
    /// is resolved once and every later stage acts on that same set, so a file appearing (or vanishing) mid-run
    /// is never half-processed - it belongs to the next run, which resolves a fresh snapshot. The schema map is
    /// concurrent because the data pass prefetches the next file's schema while the current file streams.
    /// </summary>
    private sealed class RunState
    {
        /// <summary>The single resolve for the run; null until a stage starts it.</summary>
        private volatile Task<ResolvedFiles>? _resolve;

        /// <summary>Each file's schema, read exactly once per run, keyed by <see cref="FileRef.Path"/>.</summary>
        public readonly ConcurrentDictionary<string, FileSchema> FileSchemas = new(StringComparer.Ordinal);

        /// <summary>The run's snapshot when the resolve has completed successfully, else null.</summary>
        public ResolvedFiles? Snapshot
            => _resolve is { IsCompletedSuccessfully: true } resolved ? resolved.Result : null;

        /// <summary>
        /// Starts (or joins) the run's one resolve. A failed resolve is not memoized: every stage that awaited
        /// it observes the failure, but a later stage on the same state resolves afresh instead of replaying a
        /// stale error.
        /// </summary>
        public Task<ResolvedFiles> ResolveOnceAsync(Func<Task<ResolvedFiles>> resolve)
        {
            lock (FileSchemas)
            {
                if (_resolve is { } existing)
                {
                    return existing;
                }

                var started = AwaitAndUncacheOnFailureAsync(resolve);

                // A resolve that already failed (it completed synchronously) has run its cleanup before this
                // point, so memoizing it would pin the stale failure; hand it back unmemoized instead.
                if (!started.IsFaulted && !started.IsCanceled)
                {
                    _resolve = started;
                }

                return started;
            }
        }

        private async Task<ResolvedFiles> AwaitAndUncacheOnFailureAsync(Func<Task<ResolvedFiles>> resolve)
        {
            try
            {
                return await resolve().ConfigureAwait(false);
            }
            catch
            {
                // Only this task can be memoized while it is in flight, so clearing here always removes
                // exactly this failed resolve (or a no-op null when it failed before being memoized).
                lock (FileSchemas)
                {
                    _resolve = null;
                }

                throw;
            }
        }
    }

    private sealed record ResolvedFiles(IFileStore Store, IReadOnlyList<FileRef> Files);

    /// <summary>
    /// A single file's schema: the cleaned columns the pipeline unions and maps by, plus the raw (pre-cleanup)
    /// column-name order for formats that lay out row cells by discovered name (JSON, XML). The two lists are
    /// index-aligned because <see cref="CleanColumnNames"/> is index-preserving.
    /// </summary>
    protected sealed record FileSchema(IReadOnlyList<SourceColumn> Columns, IReadOnlyList<string> RawNames);

    /// <summary>True if this reader handles the given source type (e.g. "csv", "xls").</summary>
    public abstract bool CanHandle(string sourceType);

    /// <summary>Default file glob when none is specified (e.g. "*.csv", "*.xlsx").</summary>
    protected abstract string DefaultFilePattern { get; }

    /// <summary>Maps the format's rich metadata onto the common pipeline options.</summary>
    protected abstract FileSourceOptions ReadOptions(SourceSpec source);

    /// <summary>Reads a single file's column names (header, generated, or recovered).</summary>
    protected abstract Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct);

    /// <summary>Streams a single file's rows; cells are in the same order as <see cref="ReadColumnNamesAsync"/>.</summary>
    protected abstract IAsyncEnumerable<FileLine> ReadLinesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct);

    /// <summary>
    /// Reads a single file's column schema in file order. The default treats every column as a string - the
    /// format leaves typing to the downstream inference step. A self-describing format (Parquet) overrides this
    /// to declare the real CLR and SQL types; its <see cref="ReadLinesAsync"/> cells then carry the matching
    /// typed values, which the pipeline streams straight to the typed target columns. Called at most once per
    /// file per run: the result is cached on the run state and reused by the data pass and the row streamer.
    /// </summary>
    protected virtual async Task<FileSchema> ReadFileSchemaAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var rawNames = await ReadColumnNamesAsync(store, file, source, ct).ConfigureAwait(false);
        var names = CleanColumnNames(rawNames);
        var columns = new List<SourceColumn>(names.Count);
        foreach (var name in names)
        {
            columns.Add(new SourceColumn { Name = name, Type = typeof(string), IsNullable = true });
        }

        return new FileSchema(columns, rawNames);
    }

    /// <summary>
    /// The file's schema from the run's cache, reading it (and populating the cache) only on the first request.
    /// The schema pass fills the cache for every selected file, so the data pass never re-reads a header; a data
    /// pass without a preceding schema pass (a caller that opens directly) reads on demand instead.
    /// </summary>
    private async Task<FileSchema> GetOrReadFileSchemaAsync(RunState run, IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        if (run.FileSchemas.TryGetValue(file.Path, out var cached))
        {
            return cached;
        }

        var schema = await ReadFileSchemaAsync(store, file, source, ct).ConfigureAwait(false);

        // GetOrAdd, not overwrite: if two readers of the same file raced, every consumer keeps seeing the one
        // schema that won, so the union pass and the row layout can never diverge for a file.
        return run.FileSchemas.GetOrAdd(file.Path, schema);
    }

    /// <summary>
    /// The raw (pre-cleanup) column-name order the schema pass recorded for a file in this run, or null when the
    /// file's schema has not been read yet. Formats that lay out row cells by discovered name (JSON, XML) use
    /// this to emit the data pass from a single parse instead of re-discovering the column order.
    /// </summary>
    protected IReadOnlyList<string>? CachedRawColumnNames(SourceSpec source, FileRef file)
        => _runs.TryGetValue(source, out var run) && run.FileSchemas.TryGetValue(file.Path, out var schema)
            ? schema.RawNames
            : null;

    /// <summary>
    /// The legacy file-flow column-name cleanup, applied to every file source so a V3 run produces the same
    /// target column names a legacy install did (legacy did this in Shared.cs for every header-derived name,
    /// always on). Cleaning is index-preserving, so the positional row reader stays aligned. Self-describing
    /// formats apply it to their own field names by routing through here too.
    /// </summary>
    protected static IReadOnlyList<string> CleanColumnNames(IReadOnlyList<string> names)
        => LegacyColumnCleanup.CleanFileColumnNames(names);

    public async Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = ReadOptions(source);
        var run = RunStateFor(source);
        var (store, files) = await GetOrResolveAsync(run, options, ct).ConfigureAwait(false);

        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceColumns = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);

        // Schemas are read up to `readAhead` files at a time but MERGED strictly in file order, so the union's
        // column order is exactly what a one-at-a-time read produces and a flow's target column order does not
        // depend on which read finished first. A file's failure likewise surfaces when its turn comes, naming
        // the file a sequential read would have failed on.
        await foreach (var schema in ReadAheadAsync(
            files, (file, token) => GetOrReadFileSchemaAsync(run, store, file, source, token), options.ReadAhead, ct).ConfigureAwait(false))
        {
            foreach (var column in schema.Columns)
            {
                if (string.IsNullOrEmpty(column.Name))
                {
                    continue;
                }

                if (seen.Add(column.Name))
                {
                    union.Add(column.Name);
                    sourceColumns[column.Name] = column;
                }
                else
                {
                    // The same column in another file: keep the type if it agrees, else widen to a string column
                    // (the safe, lossless union across files that disagree on the type).
                    sourceColumns[column.Name] = MergeColumns(sourceColumns[column.Name], column);
                }
            }
        }

        // Provenance and key columns are values we generate and write last, so a source column that shares one
        // of their names would be silently overwritten and re-typed. Reject the ambiguity instead of losing the
        // source data; the author can rename the source field (columnMappings) or disable the system column.
        void AddSystemColumn(string name)
        {
            if (!seen.Add(name))
            {
                throw new SqlFlowException(
                    $"A source column named '{name}' collides with the enabled '{name}' provenance/key column, "
                    + "whose generated value would overwrite the source data. Rename the source column "
                    + "(columnMappings) or disable that provenance/key column.");
            }

            union.Add(name);
        }

        foreach (var systemColumn in EnabledSystemColumns(options))
        {
            AddSystemColumn(systemColumn);
        }

        if (options.IncludeFileLineNumber)
        {
            AddSystemColumn(FileLineNumberColumn);
        }

        if (options.IncludeHashKey)
        {
            AddSystemColumn(HashKeyColumn);
        }

        if (options.IncludeConcatKey)
        {
            AddSystemColumn(ConcatKeyColumn);
        }

        var hashDigestBytes = DigestByteLength(options.HashKeyType);

        // Only the system/key columns we actually generate get their real type; a disabled provenance column
        // whose name a source column happens to share stays an ordinary string source column.
        var enabledSystem = new HashSet<string>(EnabledSystemColumns(options), StringComparer.OrdinalIgnoreCase);
        if (options.IncludeFileLineNumber)
        {
            enabledSystem.Add(FileLineNumberColumn);
        }

        return union
            .Select(name =>
            {
                // Provenance and key columns are values we generate, so they get their real types. A real source
                // column keeps the type the reader declared (string for CSV/XLS/JSON/XML, the mapped SQL type for
                // a self-describing format like Parquet), merged across files above.
                if (options.IncludeHashKey && name == HashKeyColumn)
                {
                    return new SourceColumn { Name = name, Type = typeof(byte[]), SqlType = $"varbinary({hashDigestBytes})", IsNullable = false };
                }

                if (options.IncludeConcatKey && name == ConcatKeyColumn)
                {
                    return new SourceColumn { Name = name, Type = typeof(string), MaxLength = 4000, IsNullable = false };
                }

                if (enabledSystem.Contains(name))
                {
                    var system = SystemColumnType(name);
                    return new SourceColumn { Name = name, Type = system?.Type ?? typeof(string), MaxLength = system?.MaxLength, IsNullable = true };
                }

                return sourceColumns[name];
            })
            .ToList();
    }

    /// <summary>
    /// Applies <paramref name="read"/> to the files with at most <paramref name="readAhead"/> in flight, and
    /// yields the results in FILE order regardless of which finished first. This is the readahead the
    /// <c>readAhead</c> option buys on the schema pass: a source of many small remote files spends its wall
    /// clock on per-file latency, and overlapping the reads removes it without making the merge order depend
    /// on timing. An abandoned read (a failure ahead of it in the queue) is cancelled and observed, so nothing
    /// leaks and no exception goes unhandled.
    /// </summary>
    private static async IAsyncEnumerable<T> ReadAheadAsync<T>(
        IReadOnlyList<FileRef> files,
        Func<FileRef, CancellationToken, Task<T>> read,
        int readAhead,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Each read captures its own outcome, so awaiting a queued read never throws: a failure is raised at
        // the reader's position in file order, and an abandoned read cannot become an unobserved exception.
        async Task<(T? Value, Exception? Error)> ReadCapturedAsync(FileRef file)
        {
            try
            {
                return (await read(file, cts.Token).ConfigureAwait(false), null);
            }
            catch (Exception ex)
            {
                return (default, ex);
            }
        }

        var inFlight = new Queue<Task<(T? Value, Exception? Error)>>(readAhead);
        var next = 0;
        while (next < files.Count && inFlight.Count < readAhead)
        {
            inFlight.Enqueue(ReadCapturedAsync(files[next++]));
        }

        try
        {
            while (inFlight.Count > 0)
            {
                var (value, error) = await inFlight.Dequeue().ConfigureAwait(false);
                if (error is not null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }

                if (next < files.Count)
                {
                    inFlight.Enqueue(ReadCapturedAsync(files[next++]));
                }

                yield return value!;
            }
        }
        finally
        {
            if (inFlight.Count > 0)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                while (inFlight.Count > 0)
                {
                    await inFlight.Dequeue().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Reconciles the same column seen in two files: identical resolved type wins (nullability widened); any
    /// disagreement widens to an untyped string column (the flow default types it), the safe lossless union.
    /// </summary>
    private static SourceColumn MergeColumns(SourceColumn existing, SourceColumn incoming)
    {
        // The explicit SqlType is the authoritative SQL type (it already encodes precision/scale), so columns
        // that resolve to the same Type and SqlType agree even if one producer left the redundant Precision/Scale
        // metadata null (e.g. a Parquet ulong and a decimal(20,0) both map to SQL decimal(20,0)).
        if (existing.Type == incoming.Type
            && string.Equals(existing.SqlType, incoming.SqlType, StringComparison.OrdinalIgnoreCase)
            && existing.MaxLength == incoming.MaxLength)
        {
            return existing with { IsNullable = existing.IsNullable || incoming.IsNullable };
        }

        // A real type disagreement. Only a typed format (Parquet) can reach here - the string formats all declare
        // the same untyped string column, which agrees with itself above. Carry an explicit nvarchar(max) so the
        // widened column is self-typed and does not depend on the flow's default column type.
        return new SourceColumn { Name = existing.Name, Type = typeof(string), SqlType = "nvarchar(max)", IsNullable = true };
    }

    public async Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);
        var options = ReadOptions(source);
        var run = RunStateFor(source);
        var (store, files) = await GetOrResolveAsync(run, options, ct).ConfigureAwait(false);

        var manifest = new List<ProcessedFile>();
        var names = new string[columns.Count];
        var types = new Type[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            names[i] = columns[i].Name;
            types[i] = columns[i].Type;
        }

        // Resolve DataSet_DW's ambiguous same-length date reading (day-first vs month-first) once against the whole
        // resolved file set, here (not per file), so the chosen convention is known up front and can ride out on the
        // result for the run artifact and catalog.
        var dataSetSpec = options.DataSetDate.ForFileSet([.. files.Select(f => f.Name)]);

        var rows = StreamRowsAsync(run, store, files, columns, source, options, dataSetSpec, manifest, CancellationToken.None);
        var reader = new StreamingDataReader(names, types, rows.GetAsyncEnumerator(ct));
        return new SourceReadResult
        {
            Reader = reader,
            ProcessedFiles = manifest,
            DataSetConvention = options.IncludeDataSet ? dataSetSpec.Convention : null,
        };
    }

    /// <summary>A file opened (or failed) ahead of its turn: its schema, its line enumerator primed to the first
    /// line, or the error to surface when the file's turn arrives.</summary>
    private sealed class OpenedFile
    {
        public OpenedFile(FileRef file) => File = file;

        public FileRef File { get; }
        public FileSchema? Schema;
        public IAsyncEnumerator<FileLine>? Lines;
        public bool HasFirst;
        public Exception? Error;
    }

    private async IAsyncEnumerable<object?[]> StreamRowsAsync(
        RunState run,
        IFileStore store,
        IReadOnlyList<FileRef> files,
        IReadOnlyList<SourceColumn> columns,
        SourceSpec source,
        FileSourceOptions options,
        DataSetDateSpec dataSetSpec,
        List<ProcessedFile> manifest,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            indexByName[columns[i].Name] = i;
        }

        int IndexOf(string name) => indexByName.TryGetValue(name, out var idx) ? idx : -1;

        // Each provenance index is gated on its own Include flag: when the column is disabled, a source column
        // that merely shares the name is a normal data column and must not be overwritten by a generated value.
        var columnCount = columns.Count;
        var fileNameIndex = options.IncludeFileName ? IndexOf(FileNameColumn) : -1;
        var fileDateIndex = options.IncludeFileDate ? IndexOf(FileDateColumn) : -1;
        var fileRowDateIndex = options.IncludeFileRowDate ? IndexOf(FileRowDateColumn) : -1;
        var fileSizeIndex = options.IncludeFileSize ? IndexOf(FileSizeColumn) : -1;
        var dataSetIndex = options.IncludeDataSet ? IndexOf(DataSetColumn) : -1;
        var rowNumberIndex = options.IncludeRowNumber ? IndexOf(RowNumberColumn) : -1;
        var lineNumberIndex = options.IncludeFileLineNumber ? IndexOf(FileLineNumberColumn) : -1;

        var hashKeyIndex = options.IncludeHashKey ? IndexOf(HashKeyColumn) : -1;
        var concatKeyIndex = options.IncludeConcatKey ? IndexOf(ConcatKeyColumn) : -1;
        var hashInputIndices = hashKeyIndex >= 0 ? ResolveKeyInputIndices(options.HashKeyColumns, columns, IndexOf) : [];
        var concatInputIndices = concatKeyIndex >= 0 ? ResolveKeyInputIndices(options.ConcatKeyColumns, columns, IndexOf) : [];
        using var rowHasher = hashKeyIndex >= 0 ? CreateRowHasher(options.HashKeyType) : null;

        var skipEnding = Math.Max(0, options.SkipEndingDataRows);
        long total = 0;
        var reachedMax = false;

        // Bounded prefetch: while a file streams into the loader, the next `readAhead - 1` files' schema lookup
        // and open (the download, for a remote store) already run in the background, so the pipeline never
        // idles between files. Depth is the flow's `readAhead` (default 1, the long-standing behavior) because
        // the cost of an open file is a property of the format and the source: a streaming reader holds a small
        // buffer, while a format that must materialize a file to read it holds all of it. Rows are still
        // emitted strictly in file order; a prefetch failure is captured and surfaced only when that file's
        // turn arrives, and abandoned prefetches (cancellation, a prior file failing, maxRows reached) are
        // cancelled and disposed in the finally below so no download or stream leaks.
        using var prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task<OpenedFile> OpenFileAsync(FileRef file)
        {
            var opened = new OpenedFile(file);
            try
            {
                opened.Schema = await GetOrReadFileSchemaAsync(run, store, file, source, prefetchCts.Token).ConfigureAwait(false);
                opened.Lines = ReadLinesAsync(store, file, source, prefetchCts.Token).GetAsyncEnumerator(prefetchCts.Token);
                opened.HasFirst = await opened.Lines.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (opened.Lines is not null)
                {
                    // Release the failed enumerator's stream now; a dispose failure on top of the primary
                    // failure is aggregated so neither is lost.
                    try
                    {
                        await opened.Lines.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx)
                    {
                        ex = new AggregateException(ex, disposeEx);
                    }

                    opened.Lines = null;
                }

                opened.Error = ex;
            }

            return opened;
        }

        var pending = new Queue<Task<OpenedFile>>(options.ReadAhead);
        var nextToOpen = 0;
        while (nextToOpen < files.Count && pending.Count < options.ReadAhead)
        {
            pending.Enqueue(OpenFileAsync(files[nextToOpen++]));
        }

        try
        {
            while (pending.Count > 0 && !reachedMax)
            {
                var current = await pending.Dequeue().ConfigureAwait(false);
                if (nextToOpen < files.Count)
                {
                    pending.Enqueue(OpenFileAsync(files[nextToOpen++]));
                }

                try
                {
                    if (current.Error is not null)
                    {
                        ExceptionDispatchInfo.Capture(current.Error).Throw();
                    }

                    var file = current.File;
                    var fileModifiedUtc = (file.Modified ?? DateTimeOffset.UtcNow).UtcDateTime;
                    var ingestedUtc = DateTime.UtcNow;
                    var nameValue = options.ShowPathWithFileName ? file.Path : file.Name;

                    // Provenance values are constant per file, so encode them once here rather than per row. The
                    // DataSet date is the date detected in the file NAME (legacy pre-ingestion behavior) or, when
                    // none is found or the flow opted out, the file's last-modified timestamp. The string encodings
                    // match the transformation view's casts:
                    // FileDate_DW/DataSet_DW as yyyyMMddHHmmss (view CASTs to decimal(14,0)/numeric(14,0)),
                    // FileRowDate_DW as yyyy-MM-dd HH:mm:ss (view CONVERTs to datetime, style 20), FileSize_DW as digits.
                    //
                    // FileDate_DW is the file's BUSINESS timestamp when the flow configures a fileDate source
                    // (fileDate.from: name/path), and its last-modified time otherwise. The distinction matters
                    // because last-modified is a property of the STORAGE, not of the data: a server-side copy
                    // between accounts, a lifecycle tier move or a re-upload rewrites it, and object stores do not
                    // let it be set back. A watermark resting on it therefore treats an entire migrated history as
                    // new the first time the lake is moved, and a --from/--to backfill cannot address a period at
                    // all. Reading the stamp out of the file's own name keeps the watermark, the stored provenance
                    // and a backfill window on one clock that belongs to the data.
                    var fileDateUtc = ResolveFileDate(options.FileDate, file, fileModifiedUtc);
                    var dataSetUtc = dataSetSpec.Resolve(file.Name, fileModifiedUtc);
                    var fileDateValue = fileDateUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    var fileRowDateValue = ingestedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var fileSizeValue = file.Size.ToString(CultureInfo.InvariantCulture);
                    var dataSetValue = dataSetUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

                    var fileColumns = current.Schema!.Columns;
                    var fileColumnNames = new string[fileColumns.Count];
                    for (var i = 0; i < fileColumns.Count; i++)
                    {
                        fileColumnNames[i] = fileColumns[i].Name;
                    }

                    var map = BuildColumnMap(fileColumnNames, indexByName);
                    var window = skipEnding > 0 ? new Queue<object?[]>(skipEnding + 1) : null;
                    long fileRows = 0;

                    var lines = current.Lines!;
                    var hasLine = current.HasFirst;
                    while (hasLine)
                    {
                        if (options.MaxRows > 0 && total >= options.MaxRows)
                        {
                            reachedMax = true;
                            break;
                        }

                        var line = lines.Current;
                        var cells = line.Cells;
                        var row = new object?[columnCount];
                        for (var i = 0; i < cells.Length && i < map.Length; i++)
                        {
                            if (map[i] >= 0)
                            {
                                row[map[i]] = CoerceCell(cells[i], columns[map[i]].Type);
                            }
                        }

                        if (lineNumberIndex >= 0) row[lineNumberIndex] = line.LineNumber;
                        if (fileNameIndex >= 0) row[fileNameIndex] = nameValue;
                        if (fileDateIndex >= 0) row[fileDateIndex] = fileDateValue;
                        if (fileRowDateIndex >= 0) row[fileRowDateIndex] = fileRowDateValue;
                        if (fileSizeIndex >= 0) row[fileSizeIndex] = fileSizeValue;
                        if (dataSetIndex >= 0) row[dataSetIndex] = dataSetValue;
                        if (rowNumberIndex >= 0) row[rowNumberIndex] = line.DataRowNumber;

                        if (concatKeyIndex >= 0) row[concatKeyIndex] = BuildConcatKey(row, concatInputIndices, options.ConcatKeySeparator);
                        if (rowHasher is not null) row[hashKeyIndex] = ComputeRowHash(rowHasher, row, hashInputIndices);

                        var emit = true;
                        if (window is not null)
                        {
                            window.Enqueue(row);
                            if (window.Count <= skipEnding)
                            {
                                emit = false;
                            }
                            else
                            {
                                row = window.Dequeue();
                            }
                        }

                        if (emit)
                        {
                            total++;
                            fileRows++;
                            yield return row;
                        }

                        hasLine = await lines.MoveNextAsync().ConfigureAwait(false);
                    }

                    manifest.Add(new ProcessedFile
                    {
                        Name = file.Name,
                        Path = file.Path,
                        SizeBytes = file.Size,
                        Modified = file.Modified,
                        Rows = fileRows,
                        Columns = fileColumns.Count,
                    });
                }
                finally
                {
                    if (current.Lines is not null)
                    {
                        await current.Lines.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            if (pending.Count > 0)
            {
                // Prefetched files whose turn never came: cancel the in-flight opens, then await and dispose
                // each so nothing leaks. Their captured errors, if any, are intentionally dropped - the failure
                // (or cancellation) that ended the run is already propagating to the caller.
                prefetchCts.Cancel();
                while (pending.Count > 0)
                {
                    var abandoned = await pending.Dequeue().ConfigureAwait(false);
                    if (abandoned.Lines is not null)
                    {
                        await abandoned.Lines.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    public async Task CompleteAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = ReadOptions(source);

        // The run ends here: consume its cache so a host that re-runs a cached spec instance resolves a fresh
        // file list instead of replaying this run's snapshot.
        RunState? run = null;
        if (_runs.TryGetValue(source, out var ended))
        {
            run = ended;
            _runs.Remove(source);
        }

        if (string.IsNullOrWhiteSpace(options.CopyToPath)
            && string.IsNullOrWhiteSpace(options.ZipToPath)
            && !options.SrcDeleteIngested
            && !options.SrcDeleteAtPath)
        {
            return;
        }

        // The lifecycle acts on the same snapshot the schema and data passes read, never on a re-listed set (a
        // file that appeared after the load must not be copied or deleted as if it had been ingested). Only a
        // standalone CompleteAsync, with no prior pass on this spec, resolves the list itself.
        var files = run?.Snapshot?.Files
            ?? (await ResolveAsync(options, ct).ConfigureAwait(false)).Files;
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(options.CopyToPath))
            {
                _lifecycle.Copy(file.Path, options.CopyToPath);
            }

            if (!string.IsNullOrWhiteSpace(options.ZipToPath))
            {
                _lifecycle.Zip(file.Path, options.ZipToPath);
            }

            if (options.SrcDeleteIngested || options.SrcDeleteAtPath)
            {
                _lifecycle.Delete(file.Path);
            }
        }
    }

    /// <summary>
    /// Resolves the source files a run would read, applying every selection filter (glob, path mask, date
    /// window, incremental watermark) exactly as the load does. Exposed so format-specific tooling (e.g.
    /// JSON structure discovery) selects the same files on the one code path instead of re-listing. Shares the
    /// per-run snapshot, so repeated calls within one operation list the store once.
    /// </summary>
    protected Task<(IFileStore Store, IReadOnlyList<FileRef> Files)> ResolveFilesAsync(SourceSpec source, CancellationToken ct)
        => GetOrResolveAsync(RunStateFor(source), ReadOptions(source), ct);

    private RunState RunStateFor(SourceSpec source) => _runs.GetOrCreateValue(source);

    /// <summary>
    /// The run's file-list snapshot, resolving it on the first call and reusing it afterwards. The snapshot is
    /// per run by construction (the run state is keyed by the run's SourceSpec instance and evicted by
    /// <see cref="CompleteAsync"/>), so all three stages act on one stable set of files: a file appearing
    /// mid-run does not surface in the data pass when it was absent from the schema pass.
    /// </summary>
    private async Task<(IFileStore Store, IReadOnlyList<FileRef> Files)> GetOrResolveAsync(RunState run, FileSourceOptions options, CancellationToken ct)
    {
        var snapshot = await run.ResolveOnceAsync(async () =>
        {
            var (store, files) = await ResolveAsync(options, ct).ConfigureAwait(false);
            return new ResolvedFiles(store, files);
        }).ConfigureAwait(false);

        return (snapshot.Store, snapshot.Files);
    }

    private async Task<(IFileStore Store, IReadOnlyList<FileRef> Files)> ResolveAsync(FileSourceOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SrcPath))
        {
            throw new SqlFlowException("Source requires a 'location' (file path, folder, or URI).");
        }

        var store = _fileStores.FirstOrDefault(s => s.CanHandle(options.SrcPath))
            ?? throw new SqlFlowException($"No file store handles location '{options.SrcPath}'.");

        var pattern = string.IsNullOrWhiteSpace(options.SrcFile) ? DefaultFilePattern : options.SrcFile;

        var pathMask = CompilePathMask(options.SrcPathMask);
        var from = ParseFileDateBound(options.InitFromFileDate, "initFromFileDate");
        var to = ParseFileDateBound(options.InitToFileDate, "initToFileDate");
        var after = ParseFileDateBound(options.IncrementalAfterDate, "incrementalAfterDate");

        // The selection (path mask + date window, read where fileDate says) is pushed into the store so it can
        // prune whole out-of-window partition folders during the walk instead of listing the lake and discarding
        // most of it. When nothing is being filtered the discovery carries no filter and the store lists plainly.
        var filter = new FileDateFilter(options.FileDate, from, to, after, pathMask);
        var discovery = new FileDiscovery
        {
            Pattern = pattern,
            Recursive = options.SearchSubDirectories,
            Filter = filter.IsActive ? filter : null,
        };

        var listed = await store.ListAsync(options.SrcPath, discovery, ct).ConfigureAwait(false);

        var files = listed
            .OrderBy(f => f.Modified ?? DateTimeOffset.MinValue)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            throw NoFilesSelected(options, pattern, pathMask, from, to, after, filter.Tally);
        }

        return (store, files);
    }

    /// <summary>
    /// Explains an empty selection in terms of what the walk actually saw. "The location holds nothing" and
    /// "every file is older than the watermark" have different causes and different fixes, and the engine reports
    /// them differently, so the classification comes from the filter's tally rather than from the empty list
    /// alone, which cannot distinguish them.
    /// </summary>
    private static NoSourceFilesException NoFilesSelected(
        FileSourceOptions options,
        string pattern,
        Regex? pathMask,
        DateTime? from,
        DateTime? to,
        DateTime? after,
        FileDateTally tally)
    {
        // Nothing reached the filter and no folder was pruned: the glob matched no file anywhere under the
        // location, so the filters are irrelevant here and listing them would only misdirect. An inactive filter
        // is never handed to the store and so also tallies zero, which lands here for the same true reason.
        if (tally.Tested == 0 && tally.PrunedDirectories == 0)
        {
            return new NoSourceFilesException(
                NoSourceFilesReason.NoCandidates,
                $"No files under '{options.SrcPath}' match pattern '{pattern}'.");
        }

        // The watermark is the only date bound in play and it is what emptied the selection: nothing is new. When
        // an init window is also set, a rejected file cannot be attributed to one bound or the other, so that case
        // falls through to the general message below instead of guessing which one excluded it.
        if (after is { } watermark && from is null && to is null
            && (tally.DateRejected > 0 || tally.PrunedDirectories > 0))
        {
            return new NoSourceFilesException(
                NoSourceFilesReason.NoneAfterWatermark,
                $"No new files under '{options.SrcPath}': nothing matching pattern '{pattern}' is newer than "
                + $"{watermark.ToString("u", CultureInfo.InvariantCulture)} ({Examined(tally)}).");
        }

        var filters = new List<string> { $"pattern '{pattern}'" };
        if (pathMask is not null) filters.Add($"path mask '{options.SrcPathMask}'");
        if (options.FileDate is not null) filters.Add($"file date from {options.FileDate.Source.ToString().ToLowerInvariant()}");
        if (from is not null || to is not null) filters.Add($"date window [{options.InitFromFileDate ?? "min"} .. {options.InitToFileDate ?? "max"}]");
        if (after is not null) filters.Add($"incremental watermark (> {options.IncrementalAfterDate})");

        return new NoSourceFilesException(
            NoSourceFilesReason.NoneSelected,
            $"No files under '{options.SrcPath}' matched the filters ({string.Join(", ", filters)}); {Examined(tally)}.");
    }

    /// <summary>
    /// What the walk covered, so an empty result carries its evidence and not just its verdict: "examined 128
    /// file(s)" and "pruned 12 out-of-window folder(s)" are the difference between a filter that is working and a
    /// location that was never really read.
    /// </summary>
    private static string Examined(FileDateTally tally)
    {
        var parts = new List<string>(2);
        if (tally.Tested > 0)
        {
            parts.Add($"examined {tally.Tested} file(s)");
        }

        if (tally.PrunedDirectories > 0)
        {
            parts.Add($"pruned {tally.PrunedDirectories} out-of-window folder(s)");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Generated name for a column with no header: Column1, Column2, ... (1-based).</summary>
    protected static string GeneratedColumnName(int index) => "Column" + (index + 1).ToString(CultureInfo.InvariantCulture);

    private static int[] BuildColumnMap(IReadOnlyList<string> fileColumns, IReadOnlyDictionary<string, int> indexByName)
    {
        var map = new int[fileColumns.Count];
        for (var i = 0; i < fileColumns.Count; i++)
        {
            map[i] = indexByName.TryGetValue(fileColumns[i], out var target) ? target : -1;
        }

        return map;
    }

    private static bool IsGeneratedColumn(string name)
        => name is HashKeyColumn or ConcatKeyColumn || SystemColumnType(name) is not null;

    private static int[] ResolveKeyInputIndices(string? explicitColumns, IReadOnlyList<SourceColumn> columns, Func<string, int> indexOf)
    {
        if (!string.IsNullOrWhiteSpace(explicitColumns))
        {
            var resolved = explicitColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(indexOf)
                .Where(i => i >= 0)
                .ToArray();

            if (resolved.Length == 0)
            {
                throw new SqlFlowException($"Key columns '{explicitColumns}' did not match any column in the source.");
            }

            return resolved;
        }

        var indices = new List<int>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            if (!IsGeneratedColumn(columns[i].Name))
            {
                indices.Add(i);
            }
        }

        if (indices.Count == 0)
        {
            throw new SqlFlowException("A row key was requested but the source has no columns to derive it from.");
        }

        return indices.ToArray();
    }

    private static string BuildConcatKey(object?[] row, int[] inputs, string separator)
    {
        var parts = new string[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            parts[i] = AsString(row[inputs[i]]) ?? string.Empty;
        }

        return string.Join(separator, parts);
    }

    private static byte[] ComputeRowHash(IncrementalHash hasher, object?[] row, int[] inputs)
    {
        Span<byte> lengthPrefix = stackalloc byte[4];
        foreach (var index in inputs)
        {
            var value = AsString(row[index]);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, value?.Length ?? -1);
            hasher.AppendData(lengthPrefix);
            if (!string.IsNullOrEmpty(value))
            {
                hasher.AppendData(Encoding.UTF8.GetBytes(value));
            }
        }

        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// Maps a source cell onto its resolved target column type: an empty string is a null; a string-format cell
    /// passes through; a typed cell (Parquet) streams straight into its typed column, or is rendered as a
    /// faithful string when the target column was widened to string (a cross-file type disagreement).
    /// </summary>
    private static object? CoerceCell(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s.Length == 0 ? null : s;
        }

        return targetType == typeof(string) ? AsString(value) : value;
    }

    /// <summary>A faithful, round-trippable invariant rendering of a typed cell, for string columns and row keys.</summary>
    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "True" : "False",
        float f => f.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => FormatDateTime(dt),
        DateTimeOffset dto => FormatDateTimeOffset(dto),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => FormatTime(t),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string FormatDateTime(DateTime dt)
        => dt.Ticks % TimeSpan.TicksPerSecond == 0
            ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0');

    private static string FormatDateTimeOffset(DateTimeOffset dto)
        => dto.Ticks % TimeSpan.TicksPerSecond == 0
            ? dto.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)
            : dto.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static string FormatTime(TimeOnly t)
    {
        var s = t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Row key is an author-selected identity/dedup hash, not a security primitive.")]
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Row key is an author-selected identity/dedup hash, not a security primitive.")]
    private static IncrementalHash CreateRowHasher(string hashType) => IncrementalHash.CreateHash(NormalizeHashType(hashType) switch
    {
        "SHA256" or "SHA2256" => HashAlgorithmName.SHA256,
        "SHA1" => HashAlgorithmName.SHA1,
        "MD5" => HashAlgorithmName.MD5,
        _ => HashAlgorithmName.SHA512,
    });

    private static int DigestByteLength(string hashType) => NormalizeHashType(hashType) switch
    {
        "SHA256" or "SHA2256" => 32,
        "SHA1" => 20,
        "MD5" => 16,
        _ => 64,
    };

    private static string NormalizeHashType(string? hashType)
        => (hashType ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    // The file provenance columns land as strings (varchar(255)), matching the original SQLFlow pre table: the raw
    // landing layer is untyped, and the generated transformation view applies the real types (FileDate_DW ->
    // decimal(14,0), FileSize_DW -> decimal(18,0), FileRowDate_DW -> datetime, ...). Values are written in the
    // encodings those casts expect (see StreamRowsAsync): dates as yyyyMMddHHmmss / yyyy-MM-dd HH:mm:ss strings.
    private static (Type Type, int? MaxLength)? SystemColumnType(string name) => name switch
    {
        FileNameColumn => (typeof(string), 255),
        FileDateColumn => (typeof(string), 255),
        FileRowDateColumn => (typeof(string), 255),
        FileSizeColumn => (typeof(string), 255),
        DataSetColumn => (typeof(string), 255),
        RowNumberColumn => (typeof(long), null),
        FileLineNumberColumn => (typeof(long), null),
        _ => null,
    };

    private static IEnumerable<string> EnabledSystemColumns(FileSourceOptions options)
    {
        if (options.IncludeFileName) yield return FileNameColumn;
        if (options.IncludeFileDate) yield return FileDateColumn;
        if (options.IncludeFileRowDate) yield return FileRowDateColumn;
        if (options.IncludeFileSize) yield return FileSizeColumn;
        if (options.IncludeDataSet) yield return DataSetColumn;
        if (options.IncludeRowNumber) yield return RowNumberColumn;
    }

    private static Regex? CompilePathMask(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return null;
        }

        try
        {
            return new Regex(mask, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Invalid 'srcPathMask' regular expression '{mask}': {ex.Message}", ex);
        }
    }

    private static DateTime? ParseFileDateBound(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyyMMddHHmmss"];
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, styles, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return parsed;
        }

        throw new SqlFlowException($"Invalid '{field}' value '{value}'. Use yyyy-MM-dd or a full timestamp.");
    }
}
