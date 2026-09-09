using SqlFlow.Core.Runs;

namespace SqlFlow.Execution;

/// <summary>Writes one run's artifacts into the <c>.sqlflow</c> run history (result JSON, the canonical run.log,
/// the generated-SQL trace, and any kind-specific extras). The run already finished, so a history-write failure
/// is never fatal: it is surfaced through the optional warning sink and the write returns null. The single
/// run path and the CLI's ad-hoc verbs both write through here.</summary>
public static class RunHistory
{
    /// <summary>Writes one run's artifacts into the run history next to the flow document.</summary>
    public static string? Write(
        string flowFile, string flowName, Guid runId, IReadOnlyDictionary<string, string> artifacts, Action<string>? onWarning = null)
        => WriteAt(
            Path.GetDirectoryName(Path.GetFullPath(flowFile)) ?? Directory.GetCurrentDirectory(),
            flowName, runId, artifacts, onWarning);

    /// <summary>Writes one run's artifacts under an explicit anchor directory (the ad-hoc paths that target a
    /// chosen state directory rather than a document's folder).</summary>
    public static string? WriteAt(
        string anchorDirectory, string flowName, Guid runId, IReadOnlyDictionary<string, string> artifacts, Action<string>? onWarning = null)
    {
        try
        {
            return RunHistoryWriter.Write(anchorDirectory, flowName, runId, DateTime.UtcNow, artifacts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onWarning?.Invoke($"WARN  could not write the run history: {ex.Message}");
            return null;
        }
    }
}
