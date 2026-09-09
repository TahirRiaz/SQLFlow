using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.Export;

/// <summary>
/// Runs an export flow: resolve the source server, probe the source columns, plan the chunk SELECTs and file
/// names, then stream each chunk to a file (CSV or Parquet) on the selected destination, fanned out under a
/// thread cap. An empty chunk's file is deleted. The run is recorded to the run log (FlowType 'exp'). A set
/// PostInvokeAlias runs the named flw.Invoke flow after the files are written; without an invoke runner wired
/// (without-database mode) a set alias is surfaced as a clear error by NullInvokeRunner. Log-only catch: the
/// runner never throws, returning a failed result with the original error.
/// </summary>
public sealed class ExportFlowRunner
{
    private readonly IConnectionResolver _resolver;
    private readonly IReadOnlyList<IExportDestination> _destinations;
    private readonly IIngestionRunLog _runLog;
    private readonly IInvokeRunner _invoke;

    public ExportFlowRunner(IConnectionResolver resolver, IEnumerable<IExportDestination>? destinations = null, IIngestionRunLog? runLog = null, IInvokeRunner? invoke = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _destinations = destinations?.ToList() ?? [new LocalExportDestination()];
        _runLog = runLog ?? NullIngestionRunLog.Instance;
        _invoke = invoke ?? NullInvokeRunner.Instance;
    }

    public async Task<ExportRunResult> RunAsync(ExportFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;

        // The generated-SQL trace is captured unconditionally (the result carries it on success AND failure;
        // a failed export is exactly when the generated SQL matters) and woven into the event log at Trace
        // level, so a trace-level run.log shows each statement in its execution context.
        var trace = new List<SqlTraceEntry>();
        void Trace(string step, string? sql)
        {
            if (!string.IsNullOrWhiteSpace(sql))
            {
                var entry = new SqlTraceEntry { Sequence = trace.Count + 1, Step = step, Sql = sql };
                trace.Add(entry);
                events.Log(RunLogLevel.Trace, step, sql);
                statements.Report(entry);
            }
        }

        try
        {
            events.Log(RunLogLevel.Info, "run.start",
                $"export '{flow.SysAlias}' (flow {flow.FlowId}): {flow.Source.QualifiedName} -> {flow.TrgPath} ({flow.TrgFiletype})");

            if (string.IsNullOrWhiteSpace(flow.TrgPath))
            {
                throw new SqlFlowException($"Export flow {flow.FlowId} has no trgPath.");
            }

            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false);
            var sourceConnectionString = resolved.CanonicalString;

            // The full three-part name: the declared database is honored even when the connection's default
            // catalog differs, matching the segment planner and the stored-procedure runner.
            var from = flow.Source.QualifiedName;

            var probeSql = $"SELECT * FROM {from}";
            Trace("source.probe", probeSql);
            var columns = await ProbeColumnsAsync(sourceConnectionString, probeSql, from, ct).ConfigureAwait(false);
            events.Log(RunLogLevel.Debug, "source.probe", $"{columns.Count} column(s): {string.Join(", ", columns)}");

            var keyMax = 0;
            if (string.Equals(flow.ExportBy.Trim(), "K", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(flow.IncrementalColumn))
            {
                // Read the key-max under the same source hint (for example NOLOCK) as the data SELECTs.
                var keyMaxSql = $"SELECT MAX([{Escape(flow.IncrementalColumn!)}]) FROM {from}{ExportSegmentPlanner.TableHint(flow.SrcWithHint)}";
                Trace("source.keymax", keyMaxSql);
                keyMax = await ProbeKeyMaxAsync(sourceConnectionString, keyMaxSql, ct).ConfigureAwait(false);
            }

            // A full-table export with NoOfThreads > 1 auto-chunks on a single-column key (the flow's
            // IncrementalColumn when set, otherwise a discovered single-column source key) so the thread
            // cap actually fans out; without a usable key it stays one segment and the event says why.
            FullExportKeyRange? fullKeyRange = null;
            if (IsFullExport(flow.ExportBy) && flow.NoOfThreads > 1)
            {
                (fullKeyRange, var reason) = await ProbeFullExportKeyRangeAsync(flow, sourceConnectionString, from, Trace, ct).ConfigureAwait(false);
                if (fullKeyRange is null)
                {
                    events.Log(RunLogLevel.Info, "export.plan",
                        $"full export stays a single segment despite noOfThreads {flow.NoOfThreads}: {reason}");
                }
            }

            // A day/month export with an open bound takes that bound from the data, as legacy did: it probed the
            // source's own MIN/MAX and clamped the window to it before planning. Without this the planner falls
            // back to "the last three years", so a history export silently skips everything older than that.
            var planFlow = flow;
            if (IsDateChunked(flow.ExportBy) && !string.IsNullOrWhiteSpace(flow.DateColumn)
                && (flow.FromDate is null || flow.ToDate is null))
            {
                var dateColumn = Escape(flow.DateColumn!);
                var filter = string.IsNullOrWhiteSpace(flow.SrcFilter) ? string.Empty : " " + flow.SrcFilter.Trim();
                var boundsSql = $"SELECT MIN([{dateColumn}]), MAX([{dateColumn}]) FROM {from}"
                    + $"{ExportSegmentPlanner.TableHint(flow.SrcWithHint)} WHERE 1=1{filter}";
                Trace("source.datebounds", boundsSql);
                var (min, max) = await ProbeDateBoundsAsync(sourceConnectionString, boundsSql, flow.DateColumn!, ct).ConfigureAwait(false);
                if (min is null || max is null)
                {
                    // No dated rows to bound: the planner's default window still produces the NULL-rows segment,
                    // which is exactly what the source has to give.
                    events.Log(RunLogLevel.Info, "export.window",
                        $"source has no non-NULL [{flow.DateColumn}] to bound the window; planning the default window");
                }
                else
                {
                    planFlow = flow with { FromDate = flow.FromDate ?? min, ToDate = flow.ToDate ?? max };
                    events.Log(RunLogLevel.Info, "export.window",
                        $"window {planFlow.FromDate:yyyy-MM-dd}..{planFlow.ToDate:yyyy-MM-dd} on [{flow.DateColumn}] "
                        + $"(source range {min:yyyy-MM-dd}..{max:yyyy-MM-dd})");
                }
            }

            var segments = ExportSegmentPlanner.Plan(planFlow, columns, keyMax, startUtc, fullKeyRange);
            events.Log(RunLogLevel.Info, "export.plan",
                $"{segments.Count} segment(s), chunked by '{flow.ExportBy.Trim().ToUpperInvariant()}', {Math.Max(1, flow.NoOfThreads)} concurrent");
            foreach (var segment in segments)
            {
                Trace("export.segment", $"-- {segment.FileName}\n{segment.Sql}");
            }

            var destination = _destinations.FirstOrDefault(d => d.CanHandle(flow.TrgPath!))
                ?? throw new SqlFlowException($"No export destination handles the path '{flow.TrgPath}'.");

            var maxDegree = Math.Min(segments.Count, Math.Max(1, flow.NoOfThreads));
            using var gate = new SemaphoreSlim(maxDegree, maxDegree);
            var tasks = segments.Select(async segment =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var file = await WriteSegmentAsync(flow, segment, sourceConnectionString, destination, ct).ConfigureAwait(false);
                    events.Log(RunLogLevel.Info, "export.file", file is null
                        ? $"{segment.FileName}: empty segment, no file"
                        : $"{file.Path}: {file.Rows} row(s), {file.Bytes} byte(s)");
                    return file;
                }
                finally
                {
                    gate.Release();
                }
            });

            var written = await Task.WhenAll(tasks).ConfigureAwait(false);
            var files = written.Where(f => f is not null).Select(f => f!).ToList();
            var totalRows = files.Sum(f => f.Rows);

            // PostInvokeAlias: run the named flw.Invoke flow after the files are written. A failure it does not
            // tolerate is raised and fails the run; without an invoke runner wired, NullInvokeRunner raises a
            // clear error rather than silently skipping the requested hook.
            if (!string.IsNullOrWhiteSpace(flow.PostInvokeAlias))
            {
                events.Log(RunLogLevel.Info, "invoke.post", $"running post-invoke '{flow.PostInvokeAlias.Trim()}'");
                await _invoke.RunByAliasAsync(flow.PostInvokeAlias!.Trim(), ct).ConfigureAwait(false);
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"SUCCESS: {files.Count} file(s), {totalRows} row(s) in {duration}s");
            await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, totalRows, success: true, error: null, segments, trace), ct).ConfigureAwait(false);

            return new ExportRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                TotalRows = totalRows,
                Files = files,
                SqlTrace = trace,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SqlTrace.MarkLastFailed(trace, statements, ex.Message);
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"FAILED after {duration}s: {ex.Message}");
            try
            {
                await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, 0, success: false, error: ex.Message, [], trace), ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error.
            }

            return new ExportRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = false,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
                Error = SecretHygiene.RedactedMessage(ex),
            };
        }
    }

    private static async Task<ExportedFile?> WriteSegmentAsync(ExportFlow flow, ExportSegment segment, string sourceConnectionString, IExportDestination destination, CancellationToken ct)
    {
        var fullPath = ComposePath(flow, segment);

        await using var connection = new SqlConnection(sourceConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(segment.Sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var writer = ExportFileWriterFactory.Create(flow);
        long rows;
        var stream = await destination.OpenWriteAsync(fullPath, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            rows = await writer.WriteAsync(reader, stream, ct).ConfigureAwait(false);
        }

        // An empty chunk leaves no file (legacy parity).
        if (rows == 0)
        {
            await destination.DeleteIfExistsAsync(fullPath, ct).ConfigureAwait(false);
            return null;
        }

        // ZipTrg: replace the written file with a single-entry .zip (legacy ZipTrg). A destination that cannot
        // compress throws a clear error via the default ZipAsync, so a requested zip is never silently skipped.
        if (flow.ZipTrg)
        {
            fullPath = await destination.ZipAsync(fullPath, ct).ConfigureAwait(false);
        }

        var bytes = await destination.GetSizeAsync(fullPath, ct).ConfigureAwait(false);
        return new ExportedFile(fullPath, rows, bytes);
    }

    private static string ComposePath(ExportFlow flow, ExportSegment segment)
    {
        var basePath = flow.TrgPath!.TrimEnd('/', '\\');
        return $"{basePath}/{segment.SubFolder}{segment.FileName}.{flow.TrgFiletype}";
    }

    private static async Task<IReadOnlyList<string>> ProbeColumnsAsync(string connectionString, string probeSql, string from, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(probeSql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct).ConfigureAwait(false);

        var columns = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        if (columns.Count == 0)
        {
            throw new SqlFlowException($"Export source {from} exposes no columns.");
        }

        return columns;
    }

    private static async Task<int> ProbeKeyMaxAsync(string connectionString, string keyMaxSql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(keyMaxSql, connection) { CommandTimeout = 0 };
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    /// <summary>Reads the source's MIN/MAX of the chunk date column, so an open <c>fromDate</c>/<c>toDate</c>
    /// is bounded by the data rather than by a fixed fallback window. Both are null when the source holds no
    /// row with a non-NULL date.</summary>
    private static async Task<(DateOnly? Min, DateOnly? Max)> ProbeDateBoundsAsync(
        string connectionString, string boundsSql, string dateColumn, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(boundsSql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || await reader.IsDBNullAsync(0, ct).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (ToDateOnly(reader.GetValue(0), dateColumn), ToDateOnly(reader.GetValue(1), dateColumn));
    }

    // The chunk date column has to be a date to bound a date window; a flow pointed at some other column is a
    // configuration error, and saying so beats an InvalidCastException from deep inside the probe.
    private static DateOnly ToDateOnly(object value, string dateColumn) => value switch
    {
        DateTime dt => DateOnly.FromDateTime(dt),
        DateOnly d => d,
        DateTimeOffset dto => DateOnly.FromDateTime(dto.DateTime),
        _ => throw new SqlFlowException(
            $"Export chunk column [{dateColumn}] is {value.GetType().Name}, not a date; "
            + "day/month chunking needs a date, datetime, or datetimeoffset column."),
    };

    // The planner's day/month arm, the one bounded by a date window.
    private static bool IsDateChunked(string exportBy)
    {
        var by = exportBy.Trim().ToUpperInvariant();
        return by is "D" or "M";
    }

    // Anything the planner does not chunk by day/month/key runs its default full-table arm.
    private static bool IsFullExport(string exportBy)
    {
        var by = exportBy.Trim().ToUpperInvariant();
        return by is not "D" and not "M" and not "K";
    }

    /// <summary>
    /// Probes the chunk key for a parallel full-table export: resolves the key column and its CLR type via a
    /// KeyInfo schema read (the same cross-database three-part name the data SELECTs use), then reads the key's
    /// MIN/MAX under the flow's source hint. Returns the range, or null plus the reason parallel chunking is
    /// not possible for this flow.
    /// </summary>
    private static async Task<(FullExportKeyRange? Range, string Reason)> ProbeFullExportKeyRangeAsync(
        ExportFlow flow, string connectionString, string from, Action<string, string?> trace, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string? column;
        Type? clrType;
        var keyInfoSql = $"SELECT * FROM {from}";
        await using (var schemaCommand = new SqlCommand(keyInfoSql, connection) { CommandTimeout = 0 })
        await using (var schemaReader = await schemaCommand.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, ct).ConfigureAwait(false))
        {
            var schema = await schemaReader.GetSchemaTableAsync(ct).ConfigureAwait(false);
            if (schema is null)
            {
                return (null, "the source exposes no column metadata");
            }

            var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            var keyColumns = new List<string>();
            foreach (DataRow row in schema.Rows)
            {
                if (row["ColumnName"] is not string name || row["DataType"] is not Type type)
                {
                    continue;
                }

                types[name] = type;
                if (row["IsKey"] is bool isKey && isKey)
                {
                    keyColumns.Add(name);
                }
            }

            if (!string.IsNullOrWhiteSpace(flow.IncrementalColumn))
            {
                column = flow.IncrementalColumn.Trim();
                if (!types.TryGetValue(column, out clrType))
                {
                    return (null, $"incrementalColumn '{column}' was not found on the source");
                }
            }
            else if (keyColumns.Count == 1)
            {
                column = keyColumns[0];
                clrType = types[column];
            }
            else
            {
                return (null, keyColumns.Count == 0
                    ? "the source declares no single-column key and the flow sets no incrementalColumn"
                    : $"the source key spans {keyColumns.Count} columns; set incrementalColumn to pick the chunk key");
            }
        }

        var kind = FullExportKeyRange.KindOf(clrType);
        if (kind is null)
        {
            return (null, $"chunk key '{column}' is {clrType.Name}, not a numeric or date/time type");
        }

        // Read the key range under the same source hint (for example NOLOCK) as the data SELECTs.
        var rangeSql = $"SELECT MIN([{Escape(column)}]), MAX([{Escape(column)}]) FROM {from}{ExportSegmentPlanner.TableHint(flow.SrcWithHint)}";
        trace("source.keyrange", rangeSql);
        await using var rangeCommand = new SqlCommand(rangeSql, connection) { CommandTimeout = 0 };
        await using var rangeReader = await rangeCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await rangeReader.ReadAsync(ct).ConfigureAwait(false)
            || await rangeReader.IsDBNullAsync(0, ct).ConfigureAwait(false)
            || await rangeReader.IsDBNullAsync(1, ct).ConfigureAwait(false))
        {
            return (null, $"chunk key '{column}' has no non-null values to split on");
        }

        object min;
        object max;
        try
        {
            min = rangeReader.GetValue(0);
            max = rangeReader.GetValue(1);
        }
        catch (Exception ex) when (ex is OverflowException or System.Data.SqlTypes.SqlTypeException)
        {
            // decimal(38) keys beyond the CLR decimal range cannot be split client-side.
            return (null, $"chunk key '{column}' holds values outside the client numeric range ({ex.Message})");
        }

        return (new FullExportKeyRange { Column = column, Kind = kind.Value, Min = min, Max = max }, string.Empty);
    }

    private static IngestionRunRecord BuildRecord(
        ExportFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        long totalRows, bool success, string? error, IReadOnlyList<ExportSegment> segments, IReadOnlyList<SqlTraceEntry> trace)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"{flow.SrcServer}.{flow.Source.QualifiedName}-->{flow.TrgPath}.{flow.TrgFileName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = totalRows,
            Success = success,
            Threads = flow.NoOfThreads > 0 ? flow.NoOfThreads : null,
            SelectCmd = segments.Count > 0 ? segments[0].Sql : null,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
