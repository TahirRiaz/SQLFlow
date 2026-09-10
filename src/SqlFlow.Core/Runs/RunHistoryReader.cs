using System.Text.Json;

namespace SqlFlow.Core.Runs;

/// <summary>
/// The read side of the <c>.sqlflow</c> run history that <see cref="RunHistoryWriter"/> writes: the
/// database-free record of what a file flow has already processed, so the next incremental run can bound its
/// file selection without a catalog, a lineage graph, or any connectivity. This is the without-database
/// counterpart of reading flw.SysLog, and it is the guaranteed floor for the incremental watermark: it is
/// present in single-YAML mode and full mode alike (full mode may layer a more accurate, canonical-table
/// watermark on top).
/// </summary>
public static class RunHistoryReader
{
    /// <summary>
    /// The last-processed file watermark for a file flow: the maximum file <c>modified</c> timestamp across the
    /// processed-file manifests of the flow's retained, SUCCESSFUL <c>run.json</c> artifacts. The next run reads
    /// this back and selects only files newer than it. The maximum is taken across all retained runs (not just
    /// the latest) so a backfill run that loads older files never regresses the mark. Returns null when the flow
    /// has no run history (a first run, so load everything) or when no processed file carried a modified date. A
    /// corrupt or unreadable run folder is skipped rather than failing the caller: a bad old artifact must not
    /// break a new run, and the remaining artifacts still yield the mark.
    /// </summary>
    /// <param name="anchorDirectory">The directory the run history is anchored to (the flow document's own
    /// directory), the same anchor <see cref="RunHistoryWriter.Write"/> was given.</param>
    /// <param name="flowName">The flow name, resolved to its run-history folder by <see cref="RunHistoryWriter.SafeName"/>.</param>
    public static DateTimeOffset? LastProcessedFileDate(string anchorDirectory, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);

        var flowDirectory = Path.Combine(anchorDirectory, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName));
        if (!Directory.Exists(flowDirectory))
        {
            return null;
        }

        DateTimeOffset? max = null;
        foreach (var runFolder in Directory.EnumerateDirectories(flowDirectory))
        {
            var runJson = Path.Combine(runFolder, "run.json");
            if (!File.Exists(runJson))
            {
                continue;
            }

            var mark = TryReadRunMax(runJson);
            if (mark is { } value && (max is null || value > max))
            {
                max = value;
            }
        }

        return max;
    }

    // The maximum processed-file modified date of one successful run.json, or null when the run failed, carried
    // no processed files with a modified date, or could not be read. Only the specific IO/JSON failures are
    // absorbed (into a skip), so a genuine programming error still surfaces.
    private static DateTimeOffset? TryReadRunMax(string runJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runJsonPath));
            var root = document.RootElement;

            // A failed run's manifest is not a durable watermark: its target load did not commit, so those files
            // must be reconsidered next run.
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("processedFiles", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DateTimeOffset? max = null;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.Object
                    && file.TryGetProperty("modified", out var modified)
                    && modified.ValueKind == JsonValueKind.String
                    && modified.TryGetDateTimeOffset(out var value)
                    && (max is null || value > max))
                {
                    max = value;
                }
            }

            return max;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A held-open, deleted, or valid-JSON-wrong-shape artifact degrades to "no watermark from this run";
            // the caller falls back to the other runs (or to a full load when every run is unreadable).
            return null;
        }
    }
}
