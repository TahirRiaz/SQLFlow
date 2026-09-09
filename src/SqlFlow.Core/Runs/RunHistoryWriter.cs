using System.Globalization;

namespace SqlFlow.Core.Runs;

/// <summary>
/// The YAML-mode run history: a <c>.sqlflow</c> folder kept next to the flow document, with one folder per
/// execution holding that run's artifacts (the result JSON and the generated-SQL trace). This is the
/// without-database counterpart of flw.SysLog: every run leaves a durable, inspectable record on disk, so a
/// failed pipeline can be debugged from files alone. Layout:
/// <code>
/// .sqlflow/
///   runs/
///     &lt;flow-name&gt;/
///       &lt;yyyyMMdd-HHmmss&gt;_&lt;run-id-prefix&gt;/
///         run.json
///         trace.sql
/// </code>
/// History is pruned to the most recent <see cref="KeepRuns"/> folders per flow so the directory cannot grow
/// without bound.
/// </summary>
public static class RunHistoryWriter
{
    /// <summary>Run folders kept per flow; the oldest beyond this are pruned on each write.</summary>
    public const int KeepRuns = 50;

    /// <summary>Writes one run's artifact files and returns the run directory path.</summary>
    public static string Write(
        string anchorDirectory,
        string flowName,
        Guid runId,
        DateTime startUtc,
        IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(files);

        var flowDirectory = Path.Combine(anchorDirectory, ".sqlflow", "runs", SafeName(flowName));
        var runDirectory = Path.Combine(
            flowDirectory,
            $"{startUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{runId.ToString("N", CultureInfo.InvariantCulture)[..8]}");
        Directory.CreateDirectory(runDirectory);

        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(runDirectory, name), content);
        }

        Prune(flowDirectory);
        return runDirectory;
    }

    // The timestamp prefix makes lexicographic order chronological, so pruning is a name sort.
    private static void Prune(string flowDirectory)
    {
        var runs = Directory.GetDirectories(flowDirectory);
        if (runs.Length <= KeepRuns)
        {
            return;
        }

        foreach (var stale in runs.OrderBy(r => Path.GetFileName(r), StringComparer.Ordinal).Take(runs.Length - KeepRuns))
        {
            try
            {
                Directory.Delete(stale, recursive: true);
            }
            catch (IOException)
            {
                // A run folder held open (a viewer, a virus scanner) is skipped; it is retried next prune.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: pruning is housekeeping and never fails the run.
            }
        }
    }

    /// <summary>The filesystem-safe folder name of a flow. Public because every per-flow folder under
    /// <c>.sqlflow</c> (runs/ here, state/ for stored models) must use the one naming rule, so a flow's
    /// artifacts always line up across subfolders.</summary>
    public static string SafeName(string flowName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(flowName.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 ? "flow" : safe;
    }
}
