using System.Globalization;
using System.Text.Json;
using SqlFlow.Cli.Remote;
using SqlFlow.Core;
using SqlFlow.Core.Batch;
using SqlFlow.Core.Export;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.SourceControl;
using SqlFlow.Core.StoredProcedures;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Cli;

/// <summary>
/// Local inspection verbs that need no database and no control plane: validating a whole flow estate in one
/// pass (the CI gate: exit 0 only when every document parses, --json for a machine-readable report), and
/// browsing the on-disk run history every run writes under .sqlflow/runs (what happened in my last manual
/// runs, without a catalog).
/// </summary>
internal static class LocalInspectVerbs
{
    /// <summary>One document's validation outcome, the --json report element.</summary>
    internal sealed record ValidationResult(string File, bool Ok, string? Kind, string? Name, string? Error);

    /// <summary>
    /// Validates every flow document under <paramref name="target"/> (recursively; the .sqlflow work area is
    /// excluded) through the exact same loader a single-file validate uses, so both entry points accept and
    /// refuse identically. Text mode prints one line per file; --json emits the full report array. Exit 0 only
    /// when every document is valid, 1 otherwise (including an empty estate, which is a misconfiguration, not
    /// a success).
    /// </summary>
    public static async Task<int> ValidateEstateAsync(YamlDocumentLoader documents, string target, bool json)
    {
        var root = Path.GetFullPath(target);
        List<string> files;
        if (File.Exists(root))
        {
            // A single document still goes through here when --json asked for the report shape.
            files = [root];
            root = Path.GetDirectoryName(root)!;
        }
        else if (Directory.Exists(root))
        {
            files = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.sqlflow{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            Console.Error.WriteLine($"ERROR  '{target}' is neither a document nor a folder.");
            return 1;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"ERROR  no *.yaml/*.yml documents under {root}.");
            return 1;
        }

        var results = new List<ValidationResult>(files.Count);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            try
            {
                // Parse warnings go to stderr exactly as single-file validate routes them.
                var document = DocumentLoader.Load(documents, file, Console.Error.WriteLine);
                var (kind, name) = Describe(document);
                results.Add(new ValidationResult(relative, true, kind, name, null));
            }
            catch (SqlFlowException ex)
            {
                results.Add(new ValidationResult(relative, false, null, null, ex.Message));
            }
        }

        var broken = results.Count(r => !r.Ok);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(results, ControlPlaneClient.JsonIndented));
        }
        else
        {
            foreach (var result in results)
            {
                Console.WriteLine(result.Ok
                    ? $"OK      {result.File}  ({result.Kind} '{result.Name}')"
                    : $"BROKEN  {result.File}: {result.Error}");
            }

            Console.WriteLine($"{results.Count - broken} valid, {broken} broken of {results.Count} document(s) under {root}.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return broken == 0 ? 0 : 1;
    }

    /// <summary>The (kind, name) of a loaded document, mirroring the discriminators single-file validate prints.</summary>
    private static (string Kind, string Name) Describe(object document) => document switch
    {
        FileFlowDocument doc => ("file", doc.Flow.Name),
        IngestionFlowDocument doc => ("ing", doc.Document.Flow.SysAlias ?? doc.Document.Flow.Target.Table.Name),
        ExportFlowDocument doc => ("exp", doc.Document.Flow.SysAlias),
        StoredProcedureFlowDocument doc => ("sp", doc.Document.Flow.SysAlias),
        InvokeFlowDocument doc => ("inv", doc.Document.Definition.InvokeAlias),
        HealthCheckFlowDocument doc => ("hc", doc.Document.Flow.SysAlias),
        SourceControlFlowDocument doc => ("scm", doc.Document.Flow.SysAlias),
        BatchFlowDocument doc => ("batch", doc.Document.Flow.SysAlias),
        AcquireFlowDocument doc => ("api", doc.Flow.Name),
        CopyFlowDocument doc => ("cpy", doc.Flow.Name),
        SftpFlowDocument doc => ("sftp", doc.Flow.Name),
        CalendarFlowDocument doc => ("cal", doc.Document.Flow.SysAlias),
        TranslateFlowDocument doc => ("trl", doc.Document.Flow.SysAlias),
        _ => throw new SqlFlowException($"Unhandled document kind '{document.GetType().Name}'."),
    };

    /// <summary>The slice of a run.json artifact the local listing shows.</summary>
    internal sealed record LocalRunRow(
        string? FlowKind, string? FlowName, Guid RunId, bool Success, DateTime WrittenUtc, string? Error, string Directory);

    /// <summary>
    /// 'sqlflow runs local [folder]': the on-disk run history, newest first, read straight from the
    /// .sqlflow/runs artifacts next to the pipeline files (no catalog, no control plane). --flow filters by
    /// substring, --last bounds the listing (default 50), --json emits the rows for scripting. An unreadable
    /// run.json is reported and skipped, never fatal: the listing is a lens, not a gatekeeper.
    /// </summary>
    public static async Task<int> ListLocalRunsAsync(string[] positional, string[] args)
    {
        var root = Path.GetFullPath(positional.Length > 2 ? positional[2] : Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"ERROR  '{root}' is not a directory.");
            return 1;
        }

        var json = args.Contains("--json");
        var flowFilter = Program.GetOption(args, "--flow");
        var last = Program.ParseIntOption(args, 50, "--last");

        var rows = new List<LocalRunRow>();
        foreach (var runJson in Directory.EnumerateFiles(root, "run.json", SearchOption.AllDirectories))
        {
            if (!runJson.Contains($"{Path.DirectorySeparatorChar}.sqlflow{Path.DirectorySeparatorChar}runs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(runJson).ConfigureAwait(false));
                var rootElement = document.RootElement;
                rows.Add(new LocalRunRow(
                    GetString(rootElement, "flowKind"),
                    GetString(rootElement, "flowName"),
                    rootElement.TryGetProperty("runId", out var id) && id.TryGetGuid(out var runId) ? runId : Guid.Empty,
                    rootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True,
                    rootElement.TryGetProperty("writtenUtc", out var written) && written.TryGetDateTime(out var writtenUtc)
                        ? writtenUtc
                        : File.GetLastWriteTimeUtc(runJson),
                    GetString(rootElement, "error"),
                    Path.GetDirectoryName(runJson)!));
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"WARN  unreadable artifact {runJson}: {ex.Message}");
            }
        }

        var listed = rows
            .Where(r => flowFilter is null || (r.FlowName ?? string.Empty).Contains(flowFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.WrittenUtc)
            .Take(Math.Max(1, last))
            .ToList();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(listed, ControlPlaneClient.JsonIndented));
            return 0;
        }

        Console.WriteLine($"{"OUTCOME",-7}  {"KIND",-5}  {"FLOW",-36}  {"RUN",-36}  WRITTEN (UTC)");
        foreach (var row in listed)
        {
            Console.WriteLine(
                $"{(row.Success ? "ok" : "FAILED"),-7}  {row.FlowKind ?? "-",-5}  {Shorten(row.FlowName ?? "-", 36),-36}  {row.RunId,-36}  " +
                $"{row.WrittenUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            if (row.Error is { Length: > 0 })
            {
                Console.WriteLine($"         error: {Shorten(row.Error, 120)}");
            }

            Console.WriteLine($"         {row.Directory}");
        }

        Console.WriteLine($"({listed.Count} of {rows.Count} local run(s) under {root})");
        return 0;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
