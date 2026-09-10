using System.Globalization;
using System.Text;

namespace SqlFlow.Core.Ingestion;

/// <summary>One generated SQL statement captured during a run, in execution order. The trace is the
/// debuggability contract: every statement the engine generated for a run can be read back afterwards,
/// on success and (especially) on failure.</summary>
public sealed record SqlTraceEntry
{
    /// <summary>1-based position in the run's execution order.</summary>
    public required int Sequence { get; init; }

    /// <summary>When the statement was generated (UTC), stamped at capture. It is the interleave key that places
    /// the statement at its point in the run's canonical event timeline (the Events view merges statements and
    /// run events by this instant).</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The run step that produced the statement (for example "staging.create", "target.evolve",
    /// "upsert.update").</summary>
    public required string Step { get; init; }

    public required string Sql { get; init; }

    /// <summary>The failure this exact statement raised when executed, or null when it succeeded (or was never
    /// reached). Exactly one entry per failed run carries this: the statement whose execution threw. It turns the
    /// trace from "everything the run generated" into "everything the run generated, and which one broke".</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Receives each generated SQL statement as the runner executes it, so a live consumer can persist the trace
/// during the run instead of only at completion. The control-plane node backs this with a catalog writer: the
/// Statements view then streams in as the run progresses, and the trace survives even if the run's process dies
/// before its <c>run.json</c> is written. The default <see cref="NullRunStatementSink"/> records nothing (every
/// CLI run and every library caller), so the artifact projection at completion remains the single source of
/// truth at rest.
/// </summary>
public interface IRunStatementSink
{
    /// <summary>One statement was generated and is about to execute, in run order. Called at most once per
    /// <see cref="SqlTraceEntry.Sequence"/>. Must not throw: a live-persistence hiccup can never break the run.</summary>
    void Report(SqlTraceEntry entry);

    /// <summary>The statement at <paramref name="sequence"/> failed with <paramref name="errorMessage"/>. Reported
    /// from the runner's failure path so the live row is stamped with the error, mirroring the final artifact. Must
    /// not throw.</summary>
    void ReportFailure(int sequence, string errorMessage);
}

/// <summary>The no-op default sink: a run that wants no live persistence pays nothing.</summary>
public sealed class NullRunStatementSink : IRunStatementSink
{
    public static readonly NullRunStatementSink Instance = new();

    private NullRunStatementSink()
    {
    }

    public void Report(SqlTraceEntry entry)
    {
    }

    public void ReportFailure(int sequence, string errorMessage)
    {
    }
}

/// <summary>Renders an ordered SQL trace as readable text (the per-run trace file / flw.SysLog.TraceLog).</summary>
public static class SqlTrace
{
    /// <summary>Marks the most recently traced statement as the one that failed and reports it to the live sink.
    /// For a trace-then-execute runner the last traced statement is the one that was executing when it threw, so
    /// this attributes the failure precisely. A no-op when the trace is empty (the run failed before generating
    /// any SQL).</summary>
    public static void MarkLastFailed(IList<SqlTraceEntry> trace, IRunStatementSink sink, string error)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(sink);
        if (trace.Count == 0)
        {
            return;
        }

        var index = trace.Count - 1;
        trace[index] = trace[index] with { Error = error };
        sink.ReportFailure(trace[index].Sequence, error);
    }


    public static string Render(IReadOnlyList<SqlTraceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.Append("-- [").Append(entry.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append("] ").AppendLine(entry.Step);
            if (entry.Error is { } error)
            {
                sb.Append("-- !! FAILED: ").AppendLine(error.ReplaceLineEndings(" ").TrimEnd());
            }

            sb.AppendLine(entry.Sql.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
