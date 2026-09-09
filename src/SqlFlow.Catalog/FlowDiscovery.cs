using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Catalog;

/// <summary>
/// One flow document discovered in a repo during a preview-first scan: enough for a user to select it and preview it
/// before any sync writes to the catalog. <see cref="Content"/> is secret-redacted (and omitted for a pathologically
/// large file). <see cref="ParseOk"/>/<see cref="ParseError"/> report a read failure of an already-identified flow;
/// a <c>.yaml</c> that is not a flow at all is simply not listed (extension-based discovery ignores non-flow files).
/// </summary>
public sealed record DiscoveredFlow(
    string RelativePath, string? FlowName, string? Kind, long SizeBytes, bool ParseOk, string? ParseError, string? Content);

/// <summary>
/// Lists the flow documents under a materialized estate directory WITHOUT importing them, so the GUI can preview a
/// repo's flows and pick a subset before a sync activates them in the catalog. It reuses the exact same
/// <see cref="FlowSetCollector"/> the sync uses, so a flow that parses here parses on sync (one discovery/parse
/// code path). Discovery is extension-based: every <c>*.yaml</c> that parses as a flow is listed; a <c>.yaml</c>
/// that is not a flow (a library, config, or unrelated file) is ignored, never surfaced as broken. Discovery never
/// touches the database.
/// </summary>
public static class FlowDiscovery
{
    /// <summary>Content larger than this is listed and selectable but not previewed (a real flow document is
    /// kilobytes; this only guards a corrupt or hostile file from bloating the preview payload).</summary>
    public const long MaxPreviewContentBytes = 256 * 1024;

    public static IReadOnlyList<DiscoveredFlow> Discover(string estateDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        var root = Path.GetFullPath(estateDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        // The same collector the sync runs: every *.yaml that parses as a flow. Non-flow yamls are already dropped
        // there, so the preview shows exactly what a sync would import.
        var collected = new FlowSetCollector().Collect(root);

        // One entry per file: a document that projects several flow nodes (an ingestion with an embedded health
        // check) still selects as one file, and the first node for a path names it.
        var byPath = new Dictionary<string, CollectedFlow>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in collected.Flows)
        {
            byPath.TryAdd(Normalize(flow.Node.File), flow);
        }

        var results = new List<DiscoveredFlow>();
        foreach (var (relative, flow) in byPath)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, relative);
            try
            {
                var length = new FileInfo(full).Length;
                var content = length <= MaxPreviewContentBytes
                    ? SecretHygiene.RedactedMessage(File.ReadAllText(full))
                    : null;
                results.Add(new DiscoveredFlow(relative, flow.Node.Name, flow.Node.Kind, length, true, null, content));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new DiscoveredFlow(
                    relative, flow.Node.Name, flow.Node.Kind, 0, false, SecretHygiene.RedactedMessage(ex), null));
            }
        }

        return results.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
