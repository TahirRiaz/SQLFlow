using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Runs;
using SqlFlow.Core.StoredProcedures;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.StoredProcedures;

/// <summary>
/// Executes a stored-procedure flow: resolve the target server alias through the connection registry, bind the
/// flow's input parameters, run the three-part procedure on it, and record the run. It shares the run-log seam
/// with ingestion (FlowType 'sp'), so without-database mode logs nothing and with-database mode writes one
/// flw.SysLog row. A set PostInvokeAlias runs the named flw.Invoke flow after the procedure; without an invoke
/// runner wired (without-database mode) a set alias is surfaced as a clear error by NullInvokeRunner rather than
/// silently skipped. The catch keeps the original error and never throws.
///
/// Parameters reproduce the legacy flw.Parameter contract: a value is either a literal or the result of a scalar
/// query resolved immediately before the procedure runs (which is how a watermark becomes expressible), and a
/// prefetched parameter resolves first so later queries can reference it. Row counts come back the legacy way
/// too: any OUTPUT parameter the procedure declares named @Fetched/@Inserted/@Updated/@Deleted is bound
/// automatically and read into the run record.
/// </summary>
public sealed class StoredProcedureFlowRunner
{
    private readonly IConnectionResolver _resolver;
    private readonly IIngestionRunLog _runLog;
    private readonly IInvokeRunner _invoke;

    public StoredProcedureFlowRunner(IConnectionResolver resolver, IIngestionRunLog? runLog = null, IInvokeRunner? invoke = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _runLog = runLog ?? NullIngestionRunLog.Instance;
        _invoke = invoke ?? NullInvokeRunner.Instance;
    }

    public async Task<StoredProcedureRunResult> RunAsync(StoredProcedureFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;

        // The executed SQL is captured unconditionally (the result carries it on success AND failure) and
        // woven into the event log at Trace level, the same dual feed as every other runner.
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
                $"stored procedure '{flow.SysAlias}' (flow {flow.FlowId}): EXEC {flow.Procedure.QualifiedName} on '{flow.Server}'");
            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);

            var arguments = await ResolveParametersAsync(flow, resolved.CanonicalString, events, Trace, ct).ConfigureAwait(false);
            Trace("procedure.exec", RenderExec(flow.Procedure, arguments));
            var stats = await ExecuteProcedureAsync(resolved.CanonicalString, flow.Procedure, arguments, ct).ConfigureAwait(false);
            if (stats.Any)
            {
                events.Log(RunLogLevel.Info, "procedure.stats",
                    $"{stats.Fetched} fetched, {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted");
            }

            // PostInvokeAlias: run the named flw.Invoke flow after the procedure. A failure it does not tolerate
            // is raised and fails the flow; without an invoke runner wired, NullInvokeRunner raises a clear error.
            if (!string.IsNullOrWhiteSpace(flow.PostInvokeAlias))
            {
                events.Log(RunLogLevel.Info, "invoke.post", $"running post-invoke '{flow.PostInvokeAlias.Trim()}'");
                await _invoke.RunByAliasAsync(flow.PostInvokeAlias!.Trim(), ct).ConfigureAwait(false);
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"SUCCESS in {duration}s");
            await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, success: true, error: null, trace, stats), ct).ConfigureAwait(false);

            return new StoredProcedureRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
                RowsFetched = stats.Fetched,
                RowsInserted = stats.Inserted,
                RowsUpdated = stats.Updated,
                RowsDeleted = stats.Deleted,
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
                await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, success: false, error: ex.Message, trace, ProcedureStats.None), ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error, which is returned below.
            }

            return new StoredProcedureRunResult
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

    /// <summary>
    /// The four conventionally-named OUTPUT parameters the legacy engine read into flw.SysLog. A procedure that
    /// declares any of them gets it bound automatically; one that declares none is unaffected.
    /// </summary>
    private static readonly string[] StatsParameterNames = ["Fetched", "Inserted", "Updated", "Deleted"];

    /// <summary>Row counts a procedure reported through its OUTPUT parameters.</summary>
    private readonly record struct ProcedureStats(long Fetched, long Inserted, long Updated, long Deleted)
    {
        public static ProcedureStats None => default;

        /// <summary>True when the procedure reported anything at all, so a silent one logs no stats line.</summary>
        public bool Any => Fetched != 0 || Inserted != 0 || Updated != 0 || Deleted != 0;
    }

    /// <summary>
    /// Resolves every declared parameter to a concrete value. Prefetched parameters resolve first, in
    /// declaration order, and are then available to the remaining <c>selectExp</c> queries as SQL parameters,
    /// so a query can build on an earlier lookup instead of repeating it.
    /// </summary>
    private async Task<IReadOnlyList<KeyValuePair<string, object>>> ResolveParametersAsync(
        StoredProcedureFlow flow, string procedureConnection, IRunEventSink events, Action<string, string?> trace, CancellationToken ct)
    {
        if (flow.Parameters.Count == 0)
        {
            return [];
        }

        var resolved = new List<KeyValuePair<string, object>>(flow.Parameters.Count);
        var prefetched = new List<KeyValuePair<string, object>>();

        foreach (var parameter in flow.Parameters.Where(p => p.Prefetch).Concat(flow.Parameters.Where(p => !p.Prefetch)))
        {
            object value;
            if (parameter.SelectExp is null)
            {
                value = parameter.Value ?? DBNull.Value;
            }
            else
            {
                // A parameter's own server overrides the procedure's; resolving it per parameter is what makes a
                // cross-server lookup (the legacy ParamAltServer) possible.
                var connectionString = procedureConnection;
                if (parameter.Server is not null)
                {
                    var altered = await _resolver.ResolveAsync("@" + parameter.Server, ConnectionRole.Source, ct: ct).ConfigureAwait(false);
                    connectionString = altered.CanonicalString;
                }

                // Only prefetched values the query actually names are bound: passing the rest would force every
                // query to declare parameters it does not use.
                var bindings = prefetched.Where(p => MentionsParameter(parameter.SelectExp, p.Key)).ToList();
                trace($"parameter.{parameter.Name}", parameter.SelectExp);
                var scalar = await ExecuteScalarAsync(connectionString, parameter.SelectExp, bindings, ct).ConfigureAwait(false);

                value = scalar is null or DBNull
                    ? parameter.Default ?? DBNull.Value
                    : scalar;
            }

            var entry = new KeyValuePair<string, object>(parameter.Name, value);
            resolved.Add(entry);
            if (parameter.Prefetch)
            {
                prefetched.Add(entry);
            }

            events.Log(RunLogLevel.Info, "parameter.resolve", $"@{parameter.Name} = {Describe(value)}");
        }

        return resolved;
    }

    /// <summary>
    /// Whether a query references <paramref name="name"/> as a SQL parameter. The check is deliberately a
    /// whole-token match on '@name', so a prefetched '@Day' is not considered referenced by a query that only
    /// mentions '@DayOfWeek'.
    /// </summary>
    private static bool MentionsParameter(string sql, string name)
    {
        var token = "@" + name;
        var index = sql.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var after = index + token.Length;
            if (after >= sql.Length || !(char.IsLetterOrDigit(sql[after]) || sql[after] == '_' || sql[after] == '@'))
            {
                return true;
            }

            index = sql.IndexOf(token, after, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task<object?> ExecuteScalarAsync(
        string connectionString, string sql, IReadOnlyList<KeyValuePair<string, object>> bindings, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandType = CommandType.Text, CommandTimeout = 0 };
        foreach (var (name, value) in bindings)
        {
            command.Parameters.AddWithValue("@" + name, value);
        }

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task<ProcedureStats> ExecuteProcedureAsync(
        string connectionString, RelationalObject procedure, IReadOnlyList<KeyValuePair<string, object>> arguments, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var statsParameters = await ReadStatsParametersAsync(connection, procedure, ct).ConfigureAwait(false);

        await using var command = new SqlCommand(procedure.QualifiedName, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        foreach (var (name, value) in arguments)
        {
            command.Parameters.AddWithValue("@" + name, value);
        }

        foreach (var name in statsParameters)
        {
            command.Parameters.Add(new SqlParameter("@" + name, SqlDbType.BigInt) { Direction = ParameterDirection.Output });
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new ProcedureStats(
            ReadStat(command, "Fetched"), ReadStat(command, "Inserted"), ReadStat(command, "Updated"), ReadStat(command, "Deleted"));
    }

    /// <summary>
    /// Which of the four stats OUTPUT parameters this procedure actually declares. Binding one the procedure
    /// does not declare would fail the call, so the set is read from the catalog rather than assumed.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadStatsParametersAsync(
        SqlConnection connection, RelationalObject procedure, CancellationToken ct)
    {
        const string sql = """
            SELECT p.name
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.name = @proc AND s.name = @schema AND p.is_output = 1;
            """;

        // The procedure may live in another database on the same server, so the lookup is qualified.
        var qualified = sql.Replace("sys.parameters", $"[{procedure.Database.Replace("]", "]]", StringComparison.Ordinal)}].sys.parameters", StringComparison.Ordinal)
            .Replace("sys.objects", $"[{procedure.Database.Replace("]", "]]", StringComparison.Ordinal)}].sys.objects", StringComparison.Ordinal)
            .Replace("sys.schemas", $"[{procedure.Database.Replace("]", "]]", StringComparison.Ordinal)}].sys.schemas", StringComparison.Ordinal);

        await using var command = new SqlCommand(qualified, connection) { CommandType = CommandType.Text, CommandTimeout = 0 };
        command.Parameters.AddWithValue("@proc", procedure.Name);
        command.Parameters.AddWithValue("@schema", procedure.Schema);

        var declared = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0).TrimStart('@');
            var match = StatsParameterNames.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                declared.Add(match);
            }
        }

        return declared;
    }

    private static long ReadStat(SqlCommand command, string name)
    {
        var key = "@" + name;
        if (!command.Parameters.Contains(key))
        {
            return 0;
        }

        var value = command.Parameters[key].Value;
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Renders the EXEC for the SQL trace, so the run record shows the values actually bound.</summary>
    private static string RenderExec(RelationalObject procedure, IReadOnlyList<KeyValuePair<string, object>> arguments)
        => arguments.Count == 0
            ? $"EXEC {procedure.QualifiedName};"
            : $"EXEC {procedure.QualifiedName} " + string.Join(", ", arguments.Select(a => $"@{a.Key} = {Literal(a.Value)}")) + ";";

    private static string Literal(object value)
        => value switch
        {
            null or DBNull => "NULL",
            bool b => b ? "1" : "0",
            DateTime d => $"'{d:yyyy-MM-ddTHH:mm:ss.fff}'",
            string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => "'" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal) + "'",
        };

    private static string Describe(object value)
        => value is DBNull ? "NULL" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";

    private static IngestionRunRecord BuildRecord(
        StoredProcedureFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        bool success, string? error, IReadOnlyList<SqlTraceEntry> trace, ProcedureStats stats)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"-->{flow.Server}.{flow.Procedure.QualifiedName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = stats.Fetched,
            RowsInserted = stats.Inserted,
            RowsUpdated = stats.Updated,
            RowsDeleted = stats.Deleted,
            Success = success,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);
}
