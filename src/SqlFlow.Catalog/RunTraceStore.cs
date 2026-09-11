using Microsoft.EntityFrameworkCore;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Catalog;

/// <summary>How much per-run trace is stored, split by kind, and how many SQL-statement rows the retention policy
/// would reclaim: the numbers the maintenance view shows. Events are reported for context only; they are never
/// pruned, so there is no "prunable events" count.</summary>
public sealed record RunTraceStorageSummary(
    long TotalStatements,
    long TotalEvents,
    long PrunableStatements,
    int PrunableRuns);

/// <summary>
/// The per-run trace tables: the live append a node's feed writes while a run executes, and retention for the
/// per-run <b>SQL statements</b> (<see cref="CatalogRunStatement"/>) afterwards.
/// <para>
/// The live append (<see cref="AppendLiveAsync"/>) is what makes the Statements and Events views stream while a run
/// is still executing and keeps the trace durable even if the node dies before its <c>run.json</c> is written. The
/// rows are immutable and append-only: the completion projection keeps them under their stable ids and appends only
/// the tail the feed missed, so the trace stream delivers each entry exactly once.
/// </para>
/// <para>
/// Retention reclaims the generated SQL. That SQL is near-identical on every execution of a flow (it only changes
/// when a table's schema does), so keeping a fresh copy per run is almost all duplication, and it is the heaviest
/// per-run data. Run <b>events</b> (<see cref="CatalogRunEvent"/>) are deliberately out of scope: they carry the rows
/// affected and when each run executed, which stays useful for run history and analytics, so nothing here ever
/// deletes an event. The <see cref="CatalogRun"/> header (stats and error message) is likewise never touched.
/// </para>
/// A run's statements are prunable when the run finished successfully (or was cancelled/skipped, never failed), is
/// older than the grace window, and is not its pipeline's most recent run, so each pipeline keeps the statements of
/// its latest run (the current-schema copy) and of every failed run (the offending SQL), and only the older
/// duplicates are dropped. The predicate is defined once here and drives both the summary count and the batched
/// delete, so what the view reports as prunable is exactly what a cleanup removes.
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

    /// <summary>Counts what is stored (statements and events) and how many statement rows are prunable. A null
    /// <paramref name="supersededBeforeUtc"/> means retention is off (keep forever): the totals are still reported
    /// but nothing is prunable.</summary>
    public static async Task<RunTraceStorageSummary> SummarizeAsync(
        CatalogDbContext catalog, DateTime? supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var totalStatements = await catalog.RunStatements.LongCountAsync(ct).ConfigureAwait(false);
        var totalEvents = await catalog.RunEvents.LongCountAsync(ct).ConfigureAwait(false);

        if (supersededBeforeUtc is not { } before)
        {
            return new RunTraceStorageSummary(totalStatements, totalEvents, 0, 0);
        }

        var prunable = PrunableRunIds(catalog, before);
        // Count only runs that still have statements to drop, so the view never reports "N runs" with nothing behind it.
        var prunableRuns = await prunable.CountAsync(r => catalog.RunStatements.Any(s => s.RunId == r), ct).ConfigureAwait(false);
        var prunableStatements = await catalog.RunStatements
            .LongCountAsync(s => prunable.Contains(s.RunId), ct).ConfigureAwait(false);

        return new RunTraceStorageSummary(totalStatements, totalEvents, prunableStatements, prunableRuns);
    }

    /// <summary>Deletes the prunable SQL-statement rows (retention policy), in bounded batches. A null
    /// <paramref name="supersededBeforeUtc"/> (retention off) is a no-op. Events are never touched. Idempotent and
    /// safe to run concurrently across replicas: each batch simply claims whatever prunable rows remain. Returns the
    /// number of statement rows deleted.</summary>
    public static async Task<int> PruneStatementsAsync(
        CatalogDbContext catalog, DateTime? supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (supersededBeforeUtc is not { } before)
        {
            return 0;
        }

        var statementTable = QualifiedTable(catalog, typeof(CatalogRunStatement));
        var runTable = QualifiedTable(catalog, typeof(CatalogRun));
        return await DeletePrunableInBatchesAsync(catalog, statementTable, runTable, before, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes every SQL-statement row for every run, in bounded batches: the generated SQL in full,
    /// keeping the run headers (history, timing, stats, error) and every run event. This is the one-button "purge
    /// SQL statements" the maintenance view offers, distinct from the retention prune, which spares each pipeline's
    /// latest run and failed runs. Returns the number of statement rows deleted.</summary>
    public static async Task<int> PurgeAllStatementsAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var statementTable = QualifiedTable(catalog, typeof(CatalogRunStatement));
        return await DeleteAllInBatchesAsync(catalog, statementTable, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes every run-event row for every run, in bounded batches: the execution log in full, keeping the
    /// run headers (history, timing, stats, error). Events are never pruned automatically (they carry the rows
    /// affected and timing kept for analytics); this is the deliberate, operator-driven escape hatch for reclaiming
    /// them on demand. Returns the number of event rows deleted.</summary>
    public static async Task<int> PurgeAllEventsAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var eventTable = QualifiedTable(catalog, typeof(CatalogRunEvent));
        return await DeleteAllInBatchesAsync(catalog, eventTable, ct).ConfigureAwait(false);
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

    private static async Task<int> DeletePrunableInBatchesAsync(
        CatalogDbContext catalog, string statementTable, string runTable, DateTime supersededBeforeUtc, CancellationToken ct)
    {
        // DELETE TOP (N) statement rows joined to their prunable run, looped until a pass deletes fewer than the cap
        // (the tail). The join drives from the run side, so the optimizer seeks prunable runs (Status + WrittenUtc
        // index) then their statement rows (the RunStatement RunId index) instead of scanning the whole table. The
        // table names come from the model (schema-qualified) and the status values are the RunStatuses constants, so
        // the interpolation carries no injection surface; the only runtime value, the grace instant, is the bound
        // {0} parameter.
        var sql =
            $"DELETE TOP ({DeleteBatchSize}) c FROM {statementTable} AS c " +
            $"INNER JOIN {runTable} r ON r.[RunId] = c.[RunId] " +
            $"WHERE r.[Status] IN ('{RunStatuses.Succeeded}', '{RunStatuses.Cancelled}', '{RunStatuses.Skipped}') " +
            "AND r.[WrittenUtc] < {0} " +
            $"AND EXISTS (SELECT 1 FROM {runTable} nr WHERE nr.[PipelineId] = r.[PipelineId] AND nr.[WrittenUtc] > r.[WrittenUtc]);";

        var total = 0;
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

    private static async Task<int> DeleteAllInBatchesAsync(CatalogDbContext catalog, string table, CancellationToken ct)
    {
        // DELETE TOP (N) with no predicate, looped until the table is empty (a pass deletes fewer than the cap), so
        // even a huge table drains in bounded batches without one giant transaction. The table name comes from the
        // model (schema-qualified), so there is no injection surface.
        var sql = $"DELETE TOP ({DeleteBatchSize}) FROM {table};";
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await catalog.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }
}
