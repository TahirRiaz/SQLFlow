using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.Translate;
using SqlFlow.SqlServer.Export;

namespace SqlFlow.Translate;

/// <summary>
/// Runs a translation flow in its two declared phases. Phase one: resolve the source server, read every dataset
/// query into the bind index, then stream the primary query and render one document per row (or one for the
/// whole result) through the template, writing them to the selected destination in the declared layout. Phase
/// two (optional): read the saved files back and deliver them to the declared API through the shared acquisition
/// HTTP stack. The phases are strictly ordered, so the destination always holds exactly what was (or was about
/// to be) delivered. The run is recorded to the run log (FlowType 'trl'). Log-only catch: the runner never
/// throws, returning a failed result with the original error.
/// </summary>
public sealed class TranslateFlowRunner
{
    private readonly IConnectionResolver _resolver;
    private readonly ISecretResolver _secrets;
    private readonly IReadOnlyList<IExportDestination> _destinations;
    private readonly Func<AcquireReliability, HttpClient> _httpClientFactory;
    private readonly IIngestionRunLog _runLog;
    private readonly TimeProvider _time;

    public TranslateFlowRunner(
        IConnectionResolver resolver,
        ISecretResolver secrets,
        IEnumerable<IExportDestination>? destinations = null,
        Func<AcquireReliability, HttpClient>? httpClientFactory = null,
        IIngestionRunLog? runLog = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(secrets);
        _resolver = resolver;
        _secrets = secrets;
        _destinations = destinations?.ToList() ?? [new LocalExportDestination()];
        _httpClientFactory = httpClientFactory ?? HttpClientBuilder.Build;
        _runLog = runLog ?? NullIngestionRunLog.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TranslateRunResult> RunAsync(TranslateFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;

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
                $"translate '{flow.SysAlias}' (flow {flow.FlowId}): query -> {flow.Output.Path} ({flow.Output.Mode})" +
                (flow.Invoke is null ? string.Empty : $", then {flow.Invoke.Method} {flow.Invoke.Url}"));

            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false);
            var connectionString = resolved.CanonicalString;

            var destination = _destinations.FirstOrDefault(d => d.CanHandle(flow.Output.Path))
                ?? throw new SqlFlowException($"No destination handles the output path '{flow.Output.Path}'.");

            var index = new TranslateDatasetIndex();
            long totalRows = 0;

            // Per-document URL tokens are the only consumer of retained rows; everything else streams.
            var keepRows = flow.Invoke is not null && TranslateTemplateText.HasTokens(flow.Invoke.Url);
            var writer = new TranslateOutputWriter(flow, destination, startUtc, keepRows);
            await using (writer.ConfigureAwait(false))
            {
                await using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync(ct).ConfigureAwait(false);

                    foreach (var dataset in flow.Datasets)
                    {
                        Trace($"dataset.{dataset.Name}", dataset.Query);
                        var (columns, rows) = await ReadAllAsync(connection, dataset.Query, ct).ConfigureAwait(false);
                        index.AddDataset(dataset, columns, rows);
                        events.Log(RunLogLevel.Debug, "dataset.read", $"dataset '{dataset.Name}': {rows.Count} row(s)");
                    }

                    Trace("source.query", flow.Query);
                    if (flow.DocumentsPer == TranslateDocumentGrain.Row)
                    {
                        await using var command = new SqlCommand(flow.Query, connection) { CommandTimeout = 0 };
                        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                        var columns = ColumnNames(reader, "the primary query");
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            var row = Materialize(reader, columns);
                            totalRows++;
                            var document = JsonTemplateRenderer.Render(
                                flow.Template, TranslateScope.Empty.Push(row), index, flow.Nulls);
                            await writer.WriteAsync(document, row, ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var (_, rows) = await ReadAllAsync(connection, flow.Query, ct).ConfigureAwait(false);
                        totalRows = rows.Count;
                        index.SetPrimaryRows(rows);
                        var document = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty, index, flow.Nulls);
                        await writer.WriteAsync(document, row: null, ct).ConfigureAwait(false);
                    }
                }

                await writer.CompleteAsync(ct).ConfigureAwait(false);
            }

            foreach (var file in writer.Files)
            {
                events.Log(RunLogLevel.Info, "output.file", $"{file.Path}: {file.Rows} document(s), {file.Bytes} byte(s)");
            }

            long sent = 0;
            long skipped = 0;
            if (flow.Invoke is not null)
            {
                var invoker = new TranslateInvoker(_secrets, _httpClientFactory, _time);
                (sent, skipped) = await invoker.DeliverAsync(flow, writer.Manifest, destination, events, ct).ConfigureAwait(false);
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end",
                $"SUCCESS: {totalRows} row(s) -> {writer.Documents} document(s) in {writer.Files.Count} file(s)" +
                (flow.Invoke is null ? string.Empty : $", {sent} request(s) delivered") + $" in {duration}s");
            await _runLog.WriteAsync(
                BuildRecord(flow, options, runId, startUtc, endUtc, duration, totalRows, success: true, error: null, trace),
                ct).ConfigureAwait(false);

            return new TranslateRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                TotalRows = totalRows,
                Documents = writer.Documents,
                Files = writer.Files.ToList(),
                RequestsSent = sent,
                RequestsSkipped = skipped,
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
                await _runLog.WriteAsync(
                    BuildRecord(flow, options, runId, startUtc, endUtc, duration, 0, success: false, error: ex.Message, trace),
                    ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error.
            }

            return new TranslateRunResult
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

    private static async Task<(IReadOnlyList<string> Columns, List<IReadOnlyDictionary<string, object?>> Rows)> ReadAllAsync(
        SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columns = ColumnNames(reader, "a dataset query");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Materialize(reader, columns));
        }

        return (columns, rows);
    }

    /// <summary>The reader's column names, validated to be present and unique: rows materialize as name-keyed
    /// dictionaries, so an anonymous or duplicated column would silently lose data instead of failing here.</summary>
    private static IReadOnlyList<string> ColumnNames(DbDataReader reader, string queryLabel)
    {
        var names = new List<string>(reader.FieldCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SqlFlowException($"Column {i + 1} of {queryLabel} has no name; alias every computed column.");
            }

            if (!seen.Add(name))
            {
                throw new SqlFlowException($"{queryLabel} returns column '{name}' more than once; alias the duplicates.");
            }

            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyDictionary<string, object?> Materialize(DbDataReader reader, IReadOnlyList<string> columns)
    {
        var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            var value = reader.GetValue(i);
            row[columns[i]] = value is DBNull ? null : value;
        }

        return row;
    }

    private static IngestionRunRecord BuildRecord(
        TranslateFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        long totalRows, bool success, string? error, IReadOnlyList<SqlTraceEntry> trace)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"{flow.SrcServer}.query-->{flow.Output.Path}" + (flow.Invoke is null ? string.Empty : $"-->{flow.Invoke.Url}"),
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = totalRows,
            Success = success,
            SelectCmd = flow.Query,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);
}
