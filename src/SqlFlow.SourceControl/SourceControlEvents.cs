using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SourceControl;

/// <summary>
/// The source-control run's voice: turns each stage of a snapshot into a <see cref="FlowEvent"/> on the run's
/// canonical event stream, the same stream every other flow kind publishes to. That stream is what the live
/// trace panel subscribes to and what <c>run.json</c> persists, so wiring it here is what makes a snapshot
/// visible while it runs instead of only after it ends.
///
/// A null sink turns every call into a no-op, so a direct call to <see cref="SourceControlService"/> (the
/// offline tests, a CLI run with no orchestrator) costs nothing and needs no plumbing.
/// </summary>
internal sealed class SourceControlEvents
{
    private readonly IFlowEventSink? _sink;
    private readonly Guid _runId;
    private readonly string _flowName;

    public SourceControlEvents(IFlowEventSink? sink, Guid runId, string flowName)
    {
        _sink = sink;
        _runId = runId;
        _flowName = flowName;
    }

    public void Info(string stage, string message, long? rows = null, double? elapsedMs = null)
        => Publish(FlowEventLevel.Info, stage, message, rows, elapsedMs);

    public void Warn(string stage, string message) => Publish(FlowEventLevel.Warning, stage, message);

    public void Error(string stage, string message) => Publish(FlowEventLevel.Error, stage, message);

    /// <summary>
    /// The scripter's progress callback. A warning is republished as a warning event; a count reports the stage
    /// the category is at, which is the legacy per-object progress in a form that does not flood the trace (the
    /// scripter throttles its reports to a readable pace rather than emitting one per object).
    /// </summary>
    public void Progress(ScriptProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Warning is { Length: > 0 } warning)
        {
            Warn("script", warning);
            return;
        }

        Publish(FlowEventLevel.Debug, "script", Describe(progress), progress.Scripted);
    }

    /// <summary>The one line a progress report renders as: which category, and where the walk has got to in it.
    /// Enumerating a large collection is a multi-second call of its own, so it is said out loud rather than
    /// leaving the gap before the first count looking like a stall.</summary>
    private static string Describe(ScriptProgress progress)
    {
        var total = progress.Total.ToString("N0", CultureInfo.InvariantCulture);
        return progress switch
        {
            { Total: 0 } => $"{progress.Category}: enumerating.",
            { Scripted: 0 } => $"{progress.Category}: scripting {total} object(s).",
            _ => $"{progress.Category}: {progress.Scripted.ToString("N0", CultureInfo.InvariantCulture)} of {total} scripted.",
        };
    }

    private void Publish(FlowEventLevel level, string stage, string message, long? rows = null, double? elapsedMs = null)
    {
        if (_sink is null)
        {
            return;
        }

        _sink.Publish(new FlowEvent
        {
            RunId = _runId,
            FlowName = _flowName,
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Stage = stage,
            // Every message crosses into a persisted, user-visible stream, so it passes the same redaction the
            // rest of the run's diagnostics do: a connection string or a token must never reach the trace.
            Message = SecretHygiene.RedactedMessage(message),
            Rows = rows,
            ElapsedMs = elapsedMs,
        });
    }
}
