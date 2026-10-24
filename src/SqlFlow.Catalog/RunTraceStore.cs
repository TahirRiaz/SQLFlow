using Microsoft.EntityFrameworkCore;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Catalog;

/// <summary>
/// The per-run trace tables: the live append a node's feed writes while a run executes, and the cleanup of the per-run
/// <b>SQL statements</b> (<see cref="CatalogRunStatement"/>) and <b>run events</b> (<see cref="CatalogRunEvent"/>) the
/// Maintenance page offers afterwards.
/// <para>
/// The live append (<see cref="AppendLiveAsync"/>) is what makes the Statements and Events views stream while a run
/// is still executing and keeps the trace durable even if the node dies before its <c>run.json</c> is written. The
/// rows are immutable and append-only: the completion projection keeps them under their stable ids and appends only
/// the tail the feed missed, so the trace stream delivers each entry exactly once.
/// </para>
/// <para>
/// The generated SQL is near-identical on every execution of a flow (it only changes when a table's schema does, and
/// those changes are recorded on their own), so keeping a fresh copy per run is almost all duplication: removing the
/// statements of a superseded successful run loses nothing. The events are each step's rows and timing and the run's
/// log lines: removing them loses those details for the runs they belonged to (Insights' step timings, the run's
/// Events tab), while the run's own totals on its <see cref="CatalogRun"/> row stay. Neither cleanup ever touches the
/// run row.
/// </para>
/// Both cleanups take a run's rows only when the run finished successfully (or was cancelled or skipped, never failed),
/// was written before the cut-off, and is not its pipeline's most recent run, so each pipeline keeps the statements and
/// events of its latest run (the current-schema copy) and of every failed run (the offending SQL and what led to it). The
/// predicate is defined once here and drives both the count and the batched delete, so what the page reports a cleanup
/// would remove is exactly what it removes.
/// </summary>
public static class RunTraceStore
{
    // The DELETE TOP (N) batch size: large enough that draining a big backlog is a handful of round-trips, small
    // enough that each delete's transaction and lock footprint stays modest and never escalates to a table lock.
    private const int DeleteBatchSize = 5000;

    /// <summary>The Step columns of both trace tables are capped at 128 (see <see cref="CatalogDbContext"/>); a
    /// longer step name is cut to fit, exactly as the artifact projection cuts it.</summary>
    private const int MaxStepLength = 128;

    /// <summary>
    /// Appends one batch of a run's live trace, fenced on the (node, attempt) pair the batch presents: the rows are
    /// written only while the run is still executing under exactly that lease, so a node presumed dead (its run
    /// requeued and possibly handed to a successor) can never interleave its rows with the successor's. Statements
    /// and events are inserted in the batch's own order in one save; a statement failure is then stamped onto the
    /// row it names, which the node's ordering guarantees was inserted by this batch or an earlier one (a failure
    /// naming an ordinal the feed never wrote is a no-op). Returns false, writing nothing, when the fence refuses.
    /// </summary>
    public static async Task<bool> AppendLiveAsync(
        CatalogDbContext catalog, Guid runId, RunTraceBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.Node);

        var run = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running
                && r.ClaimedByNode == batch.Node && r.Attempt == batch.Attempt)
            .Select(r => new { r.RepoId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null)
        {
            return false;
        }

        if (batch.IsEmpty)
        {
            return true;
        }

        if (batch.Statements.Count > 0 || batch.Events.Count > 0)
        {
            // The control plane's pooled context is no-tracking by default and long-lived per request; add, save,
            // and clear so the rows never accumulate in the tracker across a run's many batches.
            catalog.ChangeTracker.Clear();
            foreach (var statement in batch.Statements)
            {
                catalog.RunStatements.Add(new CatalogRunStatement
                {
                    RunId = runId,
                    RepoId = run.RepoId,
                    Ordinal = statement.Ordinal,
                    TimestampUtc = statement.TimestampUtc,
                    Step = Cap(statement.Step),
                    Sql = statement.Sql,
                    Error = statement.Error,
                });
            }

            foreach (var runEvent in batch.Events)
            {
                catalog.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = runId,
                    RepoId = run.RepoId,
                    Ordinal = runEvent.Ordinal,
                    TimestampUtc = runEvent.TimestampUtc,
                    Level = runEvent.Level,
                    Step = runEvent.Step is { } step ? Cap(step) : null,
                    Message = runEvent.Message,
                    Rows = runEvent.Rows,
                    ElapsedMs = runEvent.ElapsedMs,
                });
            }

            try
            {
                await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                catalog.ChangeTracker.Clear();
            }
        }

        foreach (var failure in batch.StatementFailures)
        {
            await catalog.RunStatements
                .Where(s => s.RunId == runId && s.Ordinal == failure.Ordinal)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Error, failure.Error), ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    private static string Cap(string step) => step.Length > MaxStepLength ? step[..MaxStepLength] : step;

    /// <summary>The run ids whose SQL statements are prunable at <paramref name="supersededBeforeUtc"/> (a terminal,
    /// non-failed run, older than the grace instant, that is not its pipeline's latest). A composable query, so the
    /// summary and the delete share one definition of "prunable".</summary>
    public static IQueryable<Guid> PrunableRunIds(CatalogDbContext catalog, DateTime supersededBeforeUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Runs
            .Where(r =>
                (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Cancelled || r.Status == RunStatuses.Skipped)
                && r.WrittenUtc < supersededBeforeUtc
                && catalog.Runs.Any(nr => nr.PipelineId == r.PipelineId && nr.WrittenUtc > r.WrittenUtc))
            .Select(r => r.RunId);
    }

    /// <summary>How many SQL-statement rows a cleanup at <paramref name="supersededBeforeUtc"/> would remove.</summary>
    public static Task<long> CountPrunableStatementsAsync(
        CatalogDbContext catalog, DateTime supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var prunable = PrunableRunIds(catalog, supersededBeforeUtc);
        return catalog.RunStatements.LongCountAsync(s => prunable.Contains(s.RunId), ct);
    }

    /// <summary>How many run-event rows a cleanup at <paramref name="supersededBeforeUtc"/> would remove.</summary>
    public static Task<long> CountPrunableEventsAsync(
        CatalogDbContext catalog, DateTime supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var prunable = PrunableRunIds(catalog, supersededBeforeUtc);
        return catalog.RunEvents.LongCountAsync(e => prunable.Contains(e.RunId), ct);
    }

    /// <summary>Deletes the SQL-statement rows of the runs <see cref="PrunableRunIds"/> names at
    /// <paramref name="supersededBeforeUtc"/>, in bounded batches. Idempotent and safe to run concurrently across replicas:
    /// each batch claims whatever prunable rows remain. Returns the number of rows deleted.</summary>
    public static Task<long> PruneStatementsAsync(
        CatalogDbContext catalog, DateTime supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return DeletePrunableInBatchesAsync(
            catalog, QualifiedTable(catalog, typeof(CatalogRunStatement)), QualifiedTable(catalog, typeof(CatalogRun)), supersededBeforeUtc, ct);
    }

    /// <summary>Deletes the run-event rows of the runs <see cref="PrunableRunIds"/> names at
    /// <paramref name="supersededBeforeUtc"/>, in bounded batches, exactly as <see cref="PruneStatementsAsync"/> deletes
    /// their statements. Returns the number of rows deleted.</summary>
    public static Task<long> PruneEventsAsync(
        CatalogDbContext catalog, DateTime supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return DeletePrunableInBatchesAsync(
            catalog, QualifiedTable(catalog, typeof(CatalogRunEvent)), QualifiedTable(catalog, typeof(CatalogRun)), supersededBeforeUtc, ct);
    }

    /// <summary>The schema-qualified, bracketed table name for a mapped entity (for example
    /// <c>[catalog].[RunStatement]</c>). Read from the model so the raw delete follows the catalog's actual schema
    /// (it is not <c>dbo</c>), and keeps following it if that ever changes, instead of hardcoding it.</summary>
    private static string QualifiedTable(CatalogDbContext catalog, Type clrType)
    {
        var entity = catalog.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"No mapped entity for {clrType.Name}.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{clrType.Name} is not mapped to a table.");
        var schema = entity.GetSchema();
        return schema is null ? $"[{table}]" : $"[{schema}].[{table}]";
    }

    private static async Task<long> DeletePrunableInBatchesAsync(
        CatalogDbContext catalog, string traceTable, string runTable, DateTime supersededBeforeUtc, CancellationToken ct)
    {
        // DELETE TOP (N) trace rows joined to their prunable run, looped until a pass deletes fewer than the cap (the
        // tail). The join drives from the run side, so the optimizer seeks prunable runs (Status + WrittenUtc index) then
        // their trace rows (each trace table's RunId index) instead of scanning the whole table. The table names come from
        // the model (schema-qualified) and the status values are the RunStatuses constants, so the interpolation carries no
        // injection surface; the only runtime value, the grace instant, is the bound {0} parameter.
        var sql =
            $"DELETE TOP ({DeleteBatchSize}) c FROM {traceTable} AS c " +
            $"INNER JOIN {runTable} r ON r.[RunId] = c.[RunId] " +
            $"WHERE r.[Status] IN ('{RunStatuses.Succeeded}', '{RunStatuses.Cancelled}', '{RunStatuses.Skipped}') " +
            "AND r.[WrittenUtc] < {0} " +
            $"AND EXISTS (SELECT 1 FROM {runTable} nr WHERE nr.[PipelineId] = r.[PipelineId] AND nr.[WrittenUtc] > r.[WrittenUtc]);";

        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await catalog.Database
                .ExecuteSqlRawAsync(sql, new object[] { supersededBeforeUtc }, ct).ConfigureAwait(false);
            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }
}
