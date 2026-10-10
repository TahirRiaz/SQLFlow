using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Cli.Remote;

internal static partial class RemoteVerbs
{
    // ---- powerbi ------------------------------------------------------------------------------------------

    /// <summary>
    /// 'sqlflow powerbi extract|publish|list|remove': Power BI report specifications. 'extract' turns a <c>.pbix</c>
    /// into the specification a subscriber can declare (<c>pbix: Sales.pbix.yaml</c>); 'publish' stores one in the
    /// semantic layer instead. A report is read by the local <c>pbix-extract</c> tool when this machine has it, and
    /// otherwise by the control plane's isolated extractor, so a Windows analyst needs nothing but the CLI and a
    /// sign-in.
    /// </summary>
    public static async Task<int> PowerBiAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        using var cts = InterceptCtrlC();
        try
        {
            return sub switch
            {
                "extract" => await ExtractReportAsync(positional, args, cts.Token).ConfigureAwait(false),
                "publish" => await PublishReportAsync(positional, args, cts.Token).ConfigureAwait(false),
                "list" => await ListReportsAsync(args, cts.Token).ConfigureAwait(false),
                "remove" => await RemoveReportAsync(positional, args, cts.Token).ConfigureAwait(false),
                _ => UnknownPowerBiVerb(),
            };
        }
        catch (PbixExtractException ex)
        {
            Console.Error.WriteLine($"ERROR  {ex.Message}");
            return 1;
        }
    }

    private static int UnknownPowerBiVerb()
    {
        Console.Error.WriteLine(
            "ERROR  'powerbi' supports: extract <report.pbix>, publish <report.pbix|spec.pbix.yaml> --repo r "
            + "--subscriber s, list, remove <id>.");
        return 1;
    }

    private static async Task<int> ExtractReportAsync(string[] positional, string[] args, CancellationToken ct)
    {
        var pbix = RequireReportFile(positional, "powerbi extract", allowSpec: false);
        var reportFile = ReportLabel(args, pbix);
        var output = Program.GetOption(args, "--out", "-o") ?? pbix + ".yaml";
        var toStdout = output == "-";

        var extracted = await ExtractAnywhereAsync(pbix, reportFile, args, ct).ConfigureAwait(false);
        if (extracted is null)
        {
            return 1;
        }

        var (spec, summary, reader) = extracted.Value;
        if (toStdout)
        {
            Console.Out.Write(spec);
        }
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(output, spec, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
                .ConfigureAwait(false);
        }

        var destination = toStdout ? "standard output" : output;
        var report = toStdout ? Console.Error : Console.Out;
        report.WriteLine($"OK   read '{reportFile}' with {reader}: {Describe(summary)} -> {destination}");
        PrintWarnings(summary);
        if (!toStdout)
        {
            report.WriteLine(
                $"     Commit it and declare it on the subscriber (pbix: <path to {Path.GetFileName(output)}>, or the "
                + "folder holding it), or store it without a commit: sqlflow powerbi publish "
                + $"\"{output}\" --repo <repo> --subscriber <name>");
        }

        return 0;
    }

    private static async Task<int> PublishReportAsync(string[] positional, string[] args, CancellationToken ct)
    {
        var input = RequireReportFile(positional, "powerbi publish", allowSpec: true);
        var subscriber = Program.GetOption(args, "--subscriber")
            ?? throw new SqlFlowException("'powerbi publish' requires --subscriber <name> (a PowerBI subscriber the repo declares).");
        var isSpec = input.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || input.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
        var reportFile = ReportLabel(args, isSpec ? ReportSpecs.LabelOf(Path.GetFileName(input)) : input);
        var json = args.Contains("--json");

        string spec;
        if (isSpec)
        {
            if (new FileInfo(input).Length > ReportSpecs.MaxBytes)
            {
                throw new SqlFlowException(
                    $"'{input}' is larger than {ReportSpecs.MaxBytes / (1024 * 1024)} MB, which no report specification is.");
            }

            spec = await File.ReadAllTextAsync(input, ct).ConfigureAwait(false);

            // Checked here first so a wrong file fails with a local message before anything is sent.
            ReportSpecs.Inspect(spec, $"'{input}'");
        }
        else
        {
            var extracted = await ExtractAnywhereAsync(input, reportFile, args, ct).ConfigureAwait(false);
            if (extracted is null)
            {
                return 1;
            }

            spec = extracted.Value.Spec;
            Note(json, $"OK   read '{reportFile}' with {extracted.Value.Reader}: {Describe(extracted.Value.Summary)}");
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            var repo = await ResolveRepoAsync(client, args, token).ConfigureAwait(false);
            var result = await client.StoreSemanticReportAsync(
                new StoreSemanticReportSpecRequest(repo.Id, subscriber, reportFile, spec), token).ConfigureAwait(false);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, ControlPlaneClient.JsonIndented));
                return 0;
            }

            var stored = result.Report.Report;
            Console.WriteLine(
                $"OK   stored report '{stored.ReportFile}' for subscriber '{stored.SubscriberName}' in repo "
                + $"'{stored.RepoName}' (id {stored.Id.ToString(CultureInfo.InvariantCulture)})"
                + (result.Replaced ? ", replacing the earlier upload." : "."));
            PrintWarnings(result.Report.Summary);
            Console.WriteLine(result.SyncQueued
                ? "     The repo's sync was queued; the report's pages and model appear once it finishes."
                : $"     Repo '{stored.RepoName}' is not synced from a git source; run 'sqlflow db sync' for it to apply the report.");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> ListReportsAsync(string[] args, CancellationToken ct)
    {
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, ct, async token =>
        {
            Guid? repoId = Program.GetOption(args, "--repo") is null
                ? null
                : (await ResolveRepoAsync(client, args, token).ConfigureAwait(false)).Id;
            var reports = await client.ListSemanticReportsAsync(repoId, Program.GetOption(args, "--subscriber"), token)
                .ConfigureAwait(false);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(reports, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Console.WriteLine(
                $"{"ID",-6}  {"REPO",-20}  {"SUBSCRIBER",-24}  {"REPORT",-30}  {"ORIGIN",-9}  {"PAGES",5}  {"VISUALS",7}  {"TABLES",6}  UPDATED (UTC)");
            foreach (var report in reports)
            {
                Console.WriteLine(
                    $"{report.Id.ToString(CultureInfo.InvariantCulture),-6}  {Truncate(report.RepoName, 20),-20}  "
                    + $"{Truncate(report.SubscriberName, 24),-24}  {Truncate(report.ReportFile, 30),-30}  {report.Origin,-9}  "
                    + $"{report.Pages,5}  {report.Visuals,7}  {report.Tables,6}  {FormatUtc(report.UpdatedUtc)}"
                    + (report.SubscriberDeclared ? string.Empty : "  (subscriber no longer declared; not served)"));
            }

            Console.WriteLine($"({reports.Count} report(s))");
            return 0;
        }).ConfigureAwait(false);
    }

    private static async Task<int> RemoveReportAsync(string[] positional, string[] args, CancellationToken ct)
    {
        if (positional.Length < 3 || !long.TryParse(positional[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            Console.Error.WriteLine("ERROR  'powerbi remove' requires the report's id (see 'sqlflow powerbi list').");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        return await GuardedAsync(url, ct, async token =>
        {
            var result = await client.DeleteSemanticReportAsync(id, token).ConfigureAwait(false);
            if (result is null)
            {
                Console.Error.WriteLine($"ERROR  no stored report with id {id.ToString(CultureInfo.InvariantCulture)}.");
                return 1;
            }

            Console.WriteLine(result.SyncQueued
                ? "OK   removed; the repo's sync was queued to drop what the report contributed."
                : "OK   removed; run 'sqlflow db sync' for the repo to drop what the report contributed.");
            return 0;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a report with the local extractor when this machine has one (unless <c>--remote</c> asks otherwise), and
    /// with the control plane's isolated extractor when it does not. Null (after the error line) when neither is
    /// available.
    /// </summary>
    private static async Task<(string Spec, ReportSpecSummary Summary, string Reader)?> ExtractAnywhereAsync(
        string pbix, string reportFile, string[] args, CancellationToken ct)
    {
        var local = args.Contains("--remote") ? null : ReportSpecs.LocateExtractor();
        if (local is not null)
        {
            var spec = await Task.Run(() => ReportSpecs.Extract(local, pbix, reportFile), ct).ConfigureAwait(false);
            return (spec, ReportSpecs.Inspect(spec, $"report '{reportFile}'"), "the local pbix-extract");
        }

        if (!IsConfigured(args))
        {
            Console.Error.WriteLine(
                "ERROR  this machine has no 'pbix-extract' tool and no control plane is configured to read the report "
                + $"instead. Either set {ReportSpecs.ExtractorPathVariable} to the tool (make -C tools/pbix-extract), or "
                + "sign in to a control plane with report extraction enabled (--url or SQLFLOW_URL, then 'sqlflow login').");
            return null;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        (string, ReportSpecSummary, string)? outcome = null;
        var exit = await GuardedAsync(url, ct, async token =>
        {
            var extracted = await client.ExtractSemanticReportAsync(pbix, reportFile, token).ConfigureAwait(false);

            // The answer is validated here as well; it is written to disk or sent back as a specification.
            var spec = ReportSpecs.Normalize(extracted.Spec, "the control plane's answer");
            outcome = (spec, ReportSpecs.Inspect(spec, "the control plane's answer"), $"the control plane at {url}");
            return 0;
        }).ConfigureAwait(false);
        return exit == 0 ? outcome : null;
    }

    /// <summary>The report (or specification) path a verb was given; throws when it is missing or of the wrong kind.</summary>
    private static string RequireReportFile(string[] positional, string verb, bool allowSpec)
    {
        var kinds = allowSpec ? "<report.pbix|spec.pbix.yaml>" : "<report.pbix>";
        if (positional.Length < 3 || string.IsNullOrWhiteSpace(positional[2]))
        {
            throw new SqlFlowException($"'{verb}' requires a file: sqlflow {verb} {kinds}.");
        }

        var path = positional[2];
        if (!File.Exists(path))
        {
            throw new SqlFlowException($"'{path}' does not exist.");
        }

        var isPbix = path.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase);
        var isYaml = path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
        if (!isPbix && !(allowSpec && isYaml))
        {
            throw new SqlFlowException($"'{path}' is not a {kinds[1..^1]} file.");
        }

        return path;
    }

    /// <summary>The label the report's pages carry: <c>--report-file</c>, or the file's own name.</summary>
    private static string ReportLabel(string[] args, string path)
    {
        var label = Program.GetOption(args, "--report-file") ?? Path.GetFileName(path);
        return ReportSpecs.ReportFileProblem(label) is { } problem
            ? throw new SqlFlowException($"report file name '{label}': {problem}")
            : label;
    }

    private static string Describe(ReportSpecSummary summary)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{summary.Pages} page(s), {summary.Visuals} visual(s), {summary.Tables} model table(s) "
            + $"({summary.ResolvedTables} resolved to a warehouse table), {summary.Measures} measure(s), "
            + $"{summary.Relationships} relationship(s)");

    private static void PrintWarnings(ReportSpecSummary summary)
    {
        foreach (var warning in summary.Warnings)
        {
            Console.Error.WriteLine($"WARN  {warning}");
        }
    }
}
