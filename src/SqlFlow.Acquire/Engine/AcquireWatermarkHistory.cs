using System.Text.Json;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Reads the incremental watermark an acquisition flow resumes from: the maximum <c>watermarkAfter</c> across the
/// flow's retained, SUCCESSFUL <c>run.json</c> artifacts in the <c>.sqlflow</c> run history. Only successful runs
/// contribute, so a failed/partial run never advances the resume point; a corrupt artifact is skipped. Mirrors the
/// file-flow <see cref="RunHistoryReader"/> so acquisition incrementality needs no catalog or connectivity.
/// </summary>
public static class AcquireWatermarkHistory
{
    public static string? LastWatermark(string anchorDirectory, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);

        var flowDirectory = Path.Combine(anchorDirectory, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName));
        if (!Directory.Exists(flowDirectory))
        {
            return null;
        }

        string? max = null;
        foreach (var runFolder in Directory.EnumerateDirectories(flowDirectory))
        {
            var runJson = Path.Combine(runFolder, "run.json");
            if (!File.Exists(runJson))
            {
                continue;
            }

            max = WatermarkOrder.Max(max, TryReadWatermark(runJson));
        }

        return max;
    }

    private static string? TryReadWatermark(string runJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runJsonPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("watermarkAfter", out var watermark) && watermark.ValueKind == JsonValueKind.String)
            {
                return watermark.GetString();
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
