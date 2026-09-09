using System.Text.Json;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Lineage.Extraction;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Phase one, observed tier: reads each flow's LATEST canonical run artifact and extracts lineage from the
/// SQL the engine actually executed (the run.json SqlTrace; file flows carry their DDL list instead). This
/// is ground truth: real incremental clauses, real staging-to-target movement, hooks as they ran, with the
/// run id and time stamped on every fact: and it needs no connectivity at all. The trace is extracted as ONE
/// script per side (source-step statements run on the source server, everything else on the target), so the
/// transient staging tables the engine creates dissolve through the extractor's local-deps resolution
/// instead of polluting the graph. A corrupt or unreadable artifact is a warning, never a stop.
/// </summary>
public static class RunArtifactCollector
{
    public static CollectionResult Collect(string flowDirectory, IReadOnlyList<CollectedFlow> flows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowDirectory);
        ArgumentNullException.ThrowIfNull(flows);

        var result = new CollectionResult();
        var root = Path.GetFullPath(flowDirectory);

        // The engine writes run history next to each flow DOCUMENT, so a nested estate has one .sqlflow per
        // subfolder: every document directory is scanned, not just the estate root. Duplicate flow names
        // (already warned about by the document scan) attribute to the first document, deterministically.
        var bySafeName = new Dictionary<string, CollectedFlow>(StringComparer.OrdinalIgnoreCase);
        var runsRoots = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var flow in flows)
        {
            bySafeName.TryAdd(RunHistoryWriter.SafeName(flow.Node.Name), flow);
            var documentDirectory = Path.GetDirectoryName(Path.Combine(root, flow.Node.File)) ?? root;
            runsRoots.Add(Path.Combine(documentDirectory, ".sqlflow", "runs"));
        }

        runsRoots.Add(Path.Combine(root, ".sqlflow", "runs"));

        foreach (var runsRoot in runsRoots.Where(Directory.Exists))
        {
            foreach (var flowFolder in Directory.EnumerateDirectories(runsRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var folderName = Path.GetFileName(flowFolder);
                if (!bySafeName.TryGetValue(folderName, out var flow))
                {
                    result.Warnings.Add(
                        $"run history '{folderName}' has no matching flow document; its observations are not attributed (a renamed or deleted flow).");
                    continue;
                }

                // A maintenance flow's run history is recognized (so it raises no orphan warning) but observed
                // nothing about the data graph: its SQL reads system catalogs to script definitions, which is
                // not lineage. Excluding it here matches the declared tier, which projects no node for it.
                if (!flow.ParticipatesInLineage)
                {
                    continue;
                }

                try
                {
                    // The timestamp-prefixed folder names make lexicographic max the latest run.
                    var latest = Directory.EnumerateDirectories(flowFolder)
                        .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
                    var runJson = latest is null ? null : Path.Combine(latest, "run.json");
                    if (runJson is null || !File.Exists(runJson))
                    {
                        continue;
                    }

                    CollectRun(result, flow, runJson);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                               or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    // Unreadable folders and valid-JSON-wrong-shape artifacts alike degrade to a warning.
                    result.Warnings.Add($"{flow.Node.Name}: run artifact unreadable ({ex.Message}); observed lineage skipped for this flow.");
                }
            }
        }

        return result;
    }

    private static void CollectRun(CollectionResult result, CollectedFlow flow, string runJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(runJsonPath));
        var root = document.RootElement;

        var runId = root.TryGetProperty("runId", out var idElement) && idElement.TryGetGuid(out var parsedId)
            ? parsedId
            : (Guid?)null;
        var writtenUtc = root.TryGetProperty("writtenUtc", out var writtenElement) && writtenElement.TryGetDateTime(out var parsedWritten)
            ? parsedWritten
            : (DateTime?)null;

        if (writtenUtc is { } written && flow.FileWriteUtc > written)
        {
            result.Warnings.Add(
                $"{flow.Node.Name}: the flow document changed after its last run ({written:yyyy-MM-dd HH:mm}Z); observed lineage may be stale until the next run.");
        }

        if (!root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Source-step statements execute on the source server; everything else on the target. Each side is
        // extracted as one script so the engine's transient staging objects dissolve via local deps.
        var sourceSql = new List<string>();
        var targetSql = new List<string>();

        if (resultElement.TryGetProperty("sqlTrace", out var trace) && trace.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in trace.EnumerateArray())
            {
                var sql = entry.TryGetProperty("sql", out var sqlElement) ? sqlElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(sql))
                {
                    continue;
                }

                var step = entry.TryGetProperty("step", out var stepElement) ? stepElement.GetString() : null;
                var onSource = step is not null && step.StartsWith("source.", StringComparison.OrdinalIgnoreCase);
                (onSource ? sourceSql : targetSql).Add(sql);
            }
        }

        // File flows trace their DDL as a plain string list.
        if (resultElement.TryGetProperty("ddlExecuted", out var ddl) && ddl.ValueKind == JsonValueKind.Array)
        {
            targetSql.AddRange(ddl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        ExtractSide(result, flow, flow.SourceServerRef ?? flow.TargetServerRef, sourceSql, "trace/source", runId, writtenUtc);
        ExtractSide(result, flow, flow.TargetServerRef, targetSql, "trace/target", runId, writtenUtc);
    }

    private static void ExtractSide(
        CollectionResult result, CollectedFlow flow, string serverRef, List<string> statements, string side,
        Guid? runId, DateTime? writtenUtc)
    {
        if (statements.Count == 0)
        {
            return;
        }

        // GO joins the trace entries into batches of one script, so created-then-read-then-dropped staging
        // resolves across statements exactly as it executed and dissolves in phase two.
        var script = string.Join($"{Environment.NewLine}GO{Environment.NewLine}", statements);
        var deps = TSqlLineageExtractor.Extract(script, $"{flow.Node.Name}/{side}");
        result.Warnings.AddRange(deps.Warnings);

        // Observed identities need at least schema.name; the engine's generated SQL is fully qualified.
        result.Facts.AddRange(ScriptFactBuilder.Facts(
            deps, flow.Node.Name, viaModuleKey: null, serverRef, LineageTier.Observed,
            minimumParts: 2, runId, writtenUtc, side));
        result.ObjectArtifacts.AddRange(ScriptFactBuilder.ObjectArtifacts(deps, serverRef, LineageTier.Observed, minimumParts: 2));

        // The executed SQL is the codebase in motion: its joins, MERGE keys, and constraint clauses are
        // data-model observations of the observed tier, attributed to the flow side as the script unit.
        ScriptFactBuilder.AppendModelObservations(
            result, deps, serverRef, LineageTier.Observed, $"{flow.Node.Name}/{side}");

        // Each module the run CREATED (a transform view, or a proc/function/trigger a hook defines) gets its own
        // body lineage attributed to itself as a module, from the actually-executed DDL.
        ExtractCreatedModules(result, serverRef, deps, flow.Node.Name, side, runId, writtenUtc);
    }

    /// <summary>
    /// Attributes each module the run created its own body lineage: the observed-tier twin of how
    /// <see cref="CatalogCollector"/> harvests a live module from <c>sys.sql_modules</c>. The combined trace
    /// script joins every statement (to dissolve the engine's transient staging through local deps), so its reads
    /// are attributed to the FLOW and cannot be scoped to one module. Re-extracting each created module's own
    /// captured DDL in isolation recovers that scope, and its body reads/writes are emitted as MODULE-attributed
    /// facts (<c>flow: null</c>, <c>viaModule</c>: the module) stamped with the run's id and time. This is what
    /// draws a run-built view to the parent table its <c>SELECT</c> reads, from the DDL the run actually executed,
    /// with observed provenance and no live catalog. A module the same run created and then dropped is transient
    /// staging and contributes nothing, matching the main extraction's hygiene; the view's own script and columns
    /// are already emitted as an object artifact by the combined pass, so only the module facts are added here.
    /// </summary>
    private static void ExtractCreatedModules(
        CollectionResult result, string serverRef, Extraction.ScriptDependencies deps, string flowName, string side,
        Guid? runId, DateTime? writtenUtc)
    {
        foreach (var created in deps.CreatedObjects.Values)
        {
            if (created.Kind is not (LineageNodeKind.View or LineageNodeKind.Procedure
                    or LineageNodeKind.Function or LineageNodeKind.Trigger)
                || string.IsNullOrWhiteSpace(created.Ddl)
                || deps.CreatedThenDropped.Contains(created.Table.Key))
            {
                continue;
            }

            var moduleKey = NodeKey.For(serverRef, created.Table.Database, created.Table.Schema, created.Table.Name);
            var moduleDeps = TSqlLineageExtractor.Extract(
                created.Ddl,
                $"{flowName}/{side} module {created.Table.Schema}.{created.Table.Name}",
                defaultDatabase: created.Table.Database);
            result.Warnings.AddRange(moduleDeps.Warnings);

            // The module's own CREATE points at itself; self-facts carry nothing (the same filter the live
            // harvest applies). A one-part body reference completes to the module's own database.
            foreach (var fact in ScriptFactBuilder.Facts(
                         moduleDeps, flow: null, viaModuleKey: moduleKey, serverRef, LineageTier.Observed,
                         minimumParts: 1, runId, writtenUtc, side))
            {
                if (NodeKey.For(serverRef, fact.Database ?? created.Table.Database, fact.Schema, fact.Name) != moduleKey)
                {
                    result.Facts.Add(fact with { Database = fact.Database ?? created.Table.Database });
                }
            }
        }
    }
}
