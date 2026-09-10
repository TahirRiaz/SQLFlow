using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Calendar;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.Calendar;

/// <summary>
/// Executes a calendar-dimension flow: generate the declared range in memory, stage it, and merge it into the
/// target so the table ends up holding exactly that range and nothing else. It shares the run-log seam with
/// every other runner (FlowType 'cal'), captures its SQL on success and failure alike, and never throws: a
/// failure comes back on the result.
///
/// The merge is the whole point of re-running. Regenerating is deterministic, so a second run over an unchanged
/// range reports zero inserted and zero updated; widening the range in the flow file inserts only the new days;
/// narrowing it deletes the days that fell outside. Nothing downstream that holds a PeriodID ever sees it move.
/// </summary>
public sealed class CalendarFlowRunner
{
    /// <summary>The session-scoped staging table the generated rows are streamed into before the merge.</summary>
    private const string StagingTable = "#sqlflow_calendar_stage";

    private readonly IConnectionResolver _resolver;
    private readonly IIngestionRunLog _runLog;

    public CalendarFlowRunner(IConnectionResolver resolver, IIngestionRunLog? runLog = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _runLog = runLog ?? NullIngestionRunLog.Instance;
    }

    public async Task<CalendarRunResult> RunAsync(CalendarFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
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

        var generated = 0L;
        var observed = 0L;
        var created = false;

        try
        {
            events.Log(RunLogLevel.Info, "run.start",
                $"calendar '{flow.SysAlias}' (flow {flow.FlowId}): {flow.From:yyyy-MM-dd} to {flow.To:yyyy-MM-dd}, " +
                $"country {flow.Country}, culture {flow.Culture}, zone {flow.TimeZone}, " +
                $"fiscal year starts month {flow.FiscalYearStartMonth}, observances {flow.Observances.ToString().ToLowerInvariant()} " +
                $"-> {flow.Table.QualifiedName} on '{flow.Server}'");

            // Generation happens before any connection is opened: a bad culture, zone or range fails without
            // having touched the database at all.
            var rows = CalendarDimensionBuilder.Build(flow);
            generated = rows.Count;
            observed = rows.Count(r => r.IsHoliday == true);
            events.Log(RunLogLevel.Info, "calendar.generate",
                $"{generated} day(s) generated, {observed} carrying an observance");

            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);

            await using var connection = new SqlConnection(resolved.CanonicalString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            created = await EnsureTargetAsync(connection, flow, Trace, events, ct).ConfigureAwait(false);
            await StageAsync(connection, rows, Trace, ct).ConfigureAwait(false);
            var counts = await MergeAsync(connection, flow, Trace, ct).ConfigureAwait(false);

            events.Log(RunLogLevel.Info, "calendar.merge",
                $"{counts.Inserted} inserted, {counts.Updated} updated, {counts.Deleted} deleted");

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"SUCCESS in {duration}s");
            await _runLog.WriteAsync(
                BuildRecord(flow, options, runId, startUtc, endUtc, duration, true, null, trace, generated, counts), ct).ConfigureAwait(false);

            return new CalendarRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
                RowsGenerated = generated,
                RowsInserted = counts.Inserted,
                RowsUpdated = counts.Updated,
                RowsDeleted = counts.Deleted,
                ObservedDays = observed,
                TableCreated = created,
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
                    BuildRecord(flow, options, runId, startUtc, endUtc, duration, false, ex.Message, trace, generated, MergeCounts.None), ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error, which is returned below.
            }

            return new CalendarRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = false,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
                RowsGenerated = generated,
                ObservedDays = observed,
                TableCreated = created,
                Error = SecretHygiene.RedactedMessage(ex),
            };
        }
    }

    private readonly record struct MergeCounts(long Inserted, long Updated, long Deleted)
    {
        public static MergeCounts None => default;
    }

    /// <summary>
    /// Makes sure the schema and the target table exist, creating them when they do not. A declared
    /// <c>rebuild</c> drops the table first; without it an existing table is left in place and merged into, so
    /// re-running never disturbs a surrogate key a fact table already references.
    /// </summary>
    private static async Task<bool> EnsureTargetAsync(
        SqlConnection connection, CalendarFlow flow, Action<string, string?> trace, IRunEventSink events, CancellationToken ct)
    {
        var db = CalendarTable.Quote(flow.Table.Database);
        var schemaSql = $"""
            IF NOT EXISTS (SELECT 1 FROM {db}.sys.schemas WHERE name = @schema)
                EXEC {db}.sys.sp_executesql N'CREATE SCHEMA {CalendarTable.Quote(flow.Table.Schema)}';
            """;
        trace("target.schema", schemaSql);
        await using (var command = new SqlCommand(schemaSql, connection) { CommandTimeout = 0 })
        {
            command.Parameters.AddWithValue("@schema", flow.Table.Schema);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (flow.Rebuild)
        {
            var dropSql = $"DROP TABLE IF EXISTS {flow.Table.QualifiedName};";
            trace("target.drop", dropSql);
            await using var drop = new SqlCommand(dropSql, connection) { CommandTimeout = 0 };
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            events.Log(RunLogLevel.Info, "target.drop", $"rebuild requested: dropped {flow.Table.QualifiedName}");
        }

        var existsSql = $"SELECT CASE WHEN OBJECT_ID('{Escape(flow.Table.QualifiedName)}', 'U') IS NULL THEN 0 ELSE 1 END;";
        await using (var exists = new SqlCommand(existsSql, connection) { CommandTimeout = 0 })
        {
            var present = Convert.ToInt32(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
            if (present)
            {
                return false;
            }
        }

        var createSql = CalendarTable.CreateTableSql(flow.Table);
        trace("target.create", createSql);
        await using var create = new SqlCommand(createSql, connection) { CommandTimeout = 0 };
        await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        events.Log(RunLogLevel.Info, "target.create", $"created {flow.Table.QualifiedName}");
        return true;
    }

    /// <summary>Creates the session-scoped staging table and streams the generated rows into it.</summary>
    private static async Task StageAsync(
        SqlConnection connection, IReadOnlyList<CalendarRow> rows, Action<string, string?> trace, CancellationToken ct)
    {
        var createSql = CalendarTable.CreateStagingSql(StagingTable);
        trace("stage.create", createSql);
        await using (var create = new SqlCommand(createSql, connection) { CommandTimeout = 0 })
        {
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var table = CalendarTable.ToDataTable(rows);
        using var bulk = new SqlBulkCopy(connection)
        {
            DestinationTableName = StagingTable,
            BulkCopyTimeout = 0,
            BatchSize = 10000,
        };

        foreach (var column in CalendarTable.Columns)
        {
            bulk.ColumnMappings.Add(column.Name, column.Name);
        }

        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
        trace("stage.load", $"-- bulk copy {rows.Count} row(s) into {StagingTable}");
    }

    /// <summary>Runs the merge and reads back what it did, per action.</summary>
    private static async Task<MergeCounts> MergeAsync(
        SqlConnection connection, CalendarFlow flow, Action<string, string?> trace, CancellationToken ct)
    {
        var mergeSql = $"""
            DECLARE @changes TABLE ([Action] nvarchar(10));

            {CalendarTable.MergeSql(flow.Table, StagingTable)}

            SELECT [Action], COUNT_BIG(*) AS [Rows] FROM @changes GROUP BY [Action];
            """;

        trace("target.merge", mergeSql);

        long inserted = 0, updated = 0, deleted = 0;
        await using var command = new SqlCommand(mergeSql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var action = reader.GetString(0);
            var count = reader.GetInt64(1);
            if (string.Equals(action, "INSERT", StringComparison.OrdinalIgnoreCase))
            {
                inserted = count;
            }
            else if (string.Equals(action, "UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                updated = count;
            }
            else if (string.Equals(action, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                deleted = count;
            }
        }

        return new MergeCounts(inserted, updated, deleted);
    }

    private static string Escape(string literal) => literal.Replace("'", "''", StringComparison.Ordinal);

    private static IngestionRunRecord BuildRecord(
        CalendarFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        bool success, string? error, IReadOnlyList<SqlTraceEntry> trace, long generated, MergeCounts counts)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"-->{flow.Server}.{flow.Table.QualifiedName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = generated,
            RowsInserted = counts.Inserted,
            RowsUpdated = counts.Updated,
            RowsDeleted = counts.Deleted,
            Success = success,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);
}
