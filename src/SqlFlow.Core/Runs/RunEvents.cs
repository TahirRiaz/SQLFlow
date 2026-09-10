using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Events;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Runs;

/// <summary>
/// One canonical run event at rest: the shape written to the <c>events</c> array of <c>run.json</c> and
/// projected into the catalog's RunEvent table. It is the durable form of a <see cref="FlowEvent"/>: the file
/// the engine started reading, the watermark it resolved, the stage that finished with a row count, the decision
/// it took, the warning it raised. Generated SQL is deliberately NOT here: statements are their own stream
/// (<see cref="Ingestion.SqlTraceEntry"/>), and the two are interleaved by timestamp when a timeline is shown.
/// </summary>
public sealed record RunEventRecord
{
    public required DateTime TimestampUtc { get; init; }

    /// <summary>The event's level as a stable lowercase name (<c>trace</c>, <c>debug</c>, <c>info</c>,
    /// <c>warning</c>, <c>error</c>), so the artifact and the catalog agree without depending on enum
    /// serialization settings.</summary>
    public required string Level { get; init; }

    /// <summary>The run step or stage the event belongs to (for example <c>source.open</c>,
    /// <c>incremental</c>, <c>target.evolve</c>); null for flow-level events that have no stage.</summary>
    public string? Step { get; init; }

    public required string Message { get; init; }

    /// <summary>Row count attached to the event (a stage summary, a file read), when the emitter measured one.</summary>
    public long? Rows { get; init; }

    /// <summary>Elapsed milliseconds attached to the event (a stage summary), when the emitter measured one.</summary>
    public double? ElapsedMs { get; init; }
}

/// <summary>The stable lowercase names of <see cref="FlowEventLevel"/> used in artifacts and the catalog.</summary>
public static class RunEventLevels
{
    public const string Trace = "trace";
    public const string Debug = "debug";
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    public static string Name(FlowEventLevel level) => level switch
    {
        FlowEventLevel.Trace => Trace,
        FlowEventLevel.Debug => Debug,
        FlowEventLevel.Warning => Warning,
        FlowEventLevel.Error => Error,
        _ => Info,
    };
}

/// <summary>
/// Collects every published <see cref="FlowEvent"/> of one run as <see cref="RunEventRecord"/>s, in order, and
/// forwards each to an optional live sink (the node's catalog writer). The executor attaches one per run, so the
/// collected list becomes the <c>events</c> array of <c>run.json</c>: the artifact stays the authoritative record
/// at rest, and the live feed only fills the gap while the run executes. Thread-safe, because init-load segments
/// and parallel stages publish concurrently.
/// </summary>
public sealed class RunEventCollector : IFlowEventSink
{
    private readonly List<RunEventRecord> _records = [];
    private readonly IFlowEventSink _live;
    private readonly Lock _gate = new();

    /// <param name="live">The live sink each event is forwarded to as it happens (the node streams it into the
    /// catalog); null collects only.</param>
    public RunEventCollector(IFlowEventSink? live = null)
    {
        _live = live ?? NullFlowEventSink.Instance;
    }

    public IReadOnlyList<RunEventRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToList();
            }
        }
    }

    public void Publish(FlowEvent flowEvent)
    {
        ArgumentNullException.ThrowIfNull(flowEvent);
        var record = new RunEventRecord
        {
            TimestampUtc = flowEvent.Timestamp.UtcDateTime,
            Level = RunEventLevels.Name(flowEvent.Level),
            Step = flowEvent.Stage,
            Message = flowEvent.Message,
            Rows = flowEvent.Rows,
            ElapsedMs = flowEvent.ElapsedMs,
        };
        lock (_gate)
        {
            _records.Add(record);
        }

        _live.Publish(flowEvent);
    }
}

/// <summary>
/// The seam that folds the canonical run log into the canonical event stream: an <see cref="IRunEventSink"/> the
/// runners log through, which forwards every entry to the wrapped run log (the <see cref="RunLogger"/> still
/// applies its own level to what lands in <c>run.log</c>) and republishes Info and Debug entries as
/// <see cref="FlowEvent"/>s. Trace-level entries are exactly the generated-SQL mirrors (see
/// <see cref="RunLogLevel.Trace"/>), which already stream through the statement sink, so republishing them would
/// duplicate every statement body; they go to the log only. The bridge reports <see cref="RunLogLevel.Trace"/> as
/// its level so no runner drops an entry at the source: the event stream always carries full detail, whatever
/// verbosity the run.log was asked for.
/// </summary>
public sealed class RunLogEventBridge : IRunEventSink
{
    private readonly IRunEventSink _log;
    private readonly IFlowEventSink _events;
    private readonly Guid _runId;
    private readonly string? _flowName;

    /// <param name="log">The canonical run log (a <see cref="RunLogger"/>); every entry is forwarded to it.</param>
    /// <param name="events">Where Info/Debug entries are republished as flow events.</param>
    /// <param name="runId">The orchestrator-assigned run id stamped on republished events, when the caller knows
    /// it before the run (the node path always does); null leaves it empty, which a direct CLI run does because
    /// the runner mints the id itself.</param>
    /// <param name="flowName">The flow's display name, stamped on republished events.</param>
    public RunLogEventBridge(IRunEventSink log, IFlowEventSink events, Guid? runId = null, string? flowName = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(events);
        _log = log;
        _events = events;
        _runId = runId ?? Guid.Empty;
        _flowName = flowName;
    }

    public RunLogLevel Level => RunLogLevel.Trace;

    public void Log(RunLogLevel level, string stepName, string message)
    {
        _log.Log(level, stepName, message);
        if (level == RunLogLevel.Trace)
        {
            return;
        }

        _events.Publish(new FlowEvent
        {
            RunId = _runId,
            FlowName = _flowName,
            Timestamp = DateTimeOffset.UtcNow,
            Level = level == RunLogLevel.Debug ? FlowEventLevel.Debug : FlowEventLevel.Info,
            Stage = stepName,
            Message = message,
        });
    }
}
