using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Batch;
using SqlFlow.Core.Calendar;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Export;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Model;
using SqlFlow.Core.Profiling;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.SourceControl;
using SqlFlow.Core.State;
using SqlFlow.Core.StoredProcedures;
using SqlFlow.Azure;
using SqlFlow.Catalog;
using SqlFlow.DuckDb;
using SqlFlow.Execution;
using SqlFlow.HealthCheck;
using SqlFlow.Lineage;
using SqlFlow.Node;
using SqlFlow.Orchestration;
using SqlFlow.Providers;
using SqlFlow.SourceControl;
using SqlFlow.Sources;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Export;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.SqlServer.Profiling;
using SqlFlow.SqlServer.StoredProcedures;
using SqlFlow.Yaml;
using SqlFlow.Cli.Remote;

namespace SqlFlow.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var verbose = args.Any(a => a is "-v" or "--verbose");
        var positional = PositionalArguments(args);

        // 'healthcheck' addresses its table through --source/--object, 'auth' is a pure environment check, 'db'
        // takes a subcommand (migrate/sync/status), and the control-plane verbs (health/login/logout/trigger/
        // runs/groups) address the remote API through flags; none take a pipeline file.
        var command = positional.Length > 0 ? positional[0].ToLowerInvariant() : string.Empty;
        var needsFile = command is not ("healthcheck" or "auth" or "db" or "worker" or "runs" or "user" or "detect-unique-key"
            or "health" or "login" or "logout" or "trigger" or "groups"
            or "whoami" or "doctor" or "summary" or "nodes" or "schedules" or "repos" or "pipelines"
            or "datasources" or "search" or "completions");
        if (positional.Length < (needsFile ? 2 : 1) || args.Any(a => a is "-h" or "--help"))
        {
            PrintUsage();
            return positional.Length < (needsFile ? 2 : 1) ? 1 : 0;
        }

        var file = positional.Length > 1 ? positional[1] : string.Empty;

        // The git-ignored .sqlflow/env file supplies local-development secrets for the ${env:...} references
        // and bare-alias conventions in flow documents; the process environment (CI, Kubernetes, the
        // scheduler) always wins. Searched from the flow document's directory upward, before anything
        // resolves, for every command.
        try
        {
            // The positional may be a pipeline FILE (validate/run) or a flow FOLDER (lineage); the env file
            // anchors to whichever directory that is.
            var envAnchor = file.Length > 0 && File.Exists(file)
                ? Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory()
                : file.Length > 0 && Directory.Exists(file)
                    ? Path.GetFullPath(file)
                    : Directory.GetCurrentDirectory();
            var (appliedEnv, envFile) = LocalEnvFile.ApplyNearest(envAnchor);
            if (envFile is not null && verbose)
            {
                Console.Error.WriteLine($"env: applied {appliedEnv.Count} variable(s) from {envFile}");
            }
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        using var provider = BuildServiceProvider(verbose, json: args.Contains("--json"));
        var loader = provider.GetRequiredService<YamlFlowLoader>();
        var documents = provider.GetRequiredService<YamlDocumentLoader>();
        var runner = provider.GetRequiredService<FlowRunner>();

        try
        {
            switch (command)
            {
                case "validate":
                {
                    // A directory validates the whole estate in one pass (the CI gate), and --json switches
                    // either shape to the machine-readable report; a single file keeps its detailed line.
                    if (Directory.Exists(file) || args.Contains("--json"))
                    {
                        return await LocalInspectVerbs.ValidateEstateAsync(documents, file, args.Contains("--json")).ConfigureAwait(false);
                    }

                    switch (DocumentLoader.Load(documents, file, Console.Error.WriteLine))
                    {
                        case FileFlowDocument doc:
                            Console.WriteLine($"OK  '{doc.Flow.Name}' is valid (source: {doc.Flow.Source.Type}, target: {doc.Flow.Target.QualifiedName}).");
                            return 0;
                        case IngestionFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            Console.WriteLine($"OK  '{flow.SysAlias ?? flow.Target.Table.Name}' is valid (ingestion: {flow.Source.Table.QualifiedName} -> {flow.Target.Table.QualifiedName}).");
                            return 0;
                        }

                        case ExportFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            Console.WriteLine($"OK  '{flow.SysAlias}' is valid (export: {flow.Source.QualifiedName} -> {flow.TrgPath} as {flow.TrgFiletype}).");
                            return 0;
                        }

                        case StoredProcedureFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            Console.WriteLine($"OK  '{flow.SysAlias}' is valid (stored procedure: EXEC {flow.Procedure.QualifiedName} on '{flow.Server}').");
                            return 0;
                        }

                        case InvokeFlowDocument doc:
                        {
                            var definition = doc.Document.Definition;
                            Console.WriteLine($"OK  '{definition.InvokeAlias}' is valid (invoke: {definition.FlowType} {definition.PipelineName ?? definition.RunbookName}).");
                            return 0;
                        }

                        case HealthCheckFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            var metricNames = string.Join(", ", flow.Metrics.Select(m => m.Name));
                            Console.WriteLine($"OK  '{flow.SysAlias}' is valid (health check: {flow.Metrics.Count} metric(s) [{metricNames}] per {flow.DateColumn} on {flow.Target.QualifiedName}).");
                            return 0;
                        }

                        case SourceControlFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            var target = flow.Repository.Remote is { } remote ? remote : "(local history only)";
                            Console.WriteLine($"OK  '{flow.SysAlias}' is valid (source control: database on '{flow.Server}' -> {target} [{flow.Repository.Branch}]).");
                            return 0;
                        }

                        case CalendarFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            var days = flow.To.DayNumber - flow.From.DayNumber + 1;
                            Console.WriteLine(
                                $"OK  '{flow.SysAlias}' is valid (calendar: {flow.From:yyyy-MM-dd} to {flow.To:yyyy-MM-dd}, " +
                                $"{days} day(s), country {flow.Country} -> {flow.Table.QualifiedName} on '{flow.Server}').");
                            return 0;
                        }

                        case BatchFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            Console.WriteLine(
                                $"OK  '{flow.SysAlias}' is valid (batch: include {string.Join(", ", flow.Include)}; onError {flow.OnError.ToString().ToLowerInvariant()}; " +
                                $"maxParallel {(flow.MaxParallel <= 0 ? "unbounded" : flow.MaxParallel.ToString(System.Globalization.CultureInfo.InvariantCulture))}).");
                            return 0;
                        }

                        case AcquireFlowDocument doc:
                            Console.WriteLine(doc.Flow.Items.Count == 1
                                ? $"OK  '{doc.Flow.Name}' is valid (api: {doc.Flow.Source.Transport} -> {doc.Flow.Items[0].Landing.Target})."
                                : $"OK  '{doc.Flow.Name}' is valid (api: {doc.Flow.Source.Transport}, {doc.Flow.Items.Count} items).");
                            return 0;

                        case CopyFlowDocument doc:
                        {
                            var op = doc.Flow.Operation.ToString().ToLowerInvariant();
                            var where = doc.Flow.Steps.Count == 1
                                ? $"{doc.Flow.Steps[0].Source.Location} -> {doc.Flow.Steps[0].Target.Location}"
                                : $"{doc.Flow.Steps.Count} steps";
                            Console.WriteLine($"OK  '{doc.Flow.Name}' is valid (copy: {op} {where}).");
                            return 0;
                        }

                        case SftpFlowDocument doc:
                        {
                            var dir = doc.Flow.Direction.ToString().ToLowerInvariant();
                            var where = doc.Flow.Steps.Count == 1
                                ? $"{doc.Flow.Server.Host}:{doc.Flow.Server.Port}{doc.Flow.Steps[0].RemotePath} <-> {doc.Flow.Steps[0].Local}"
                                : $"{doc.Flow.Server.Host}:{doc.Flow.Server.Port} ({doc.Flow.Steps.Count} steps)";
                            Console.WriteLine($"OK  '{doc.Flow.Name}' is valid (sftp: {dir} {where}).");
                            return 0;
                        }

                        case TranslateFlowDocument doc:
                        {
                            var flow = doc.Document.Flow;
                            var grain = flow.DocumentsPer == SqlFlow.Core.Translate.TranslateDocumentGrain.Row ? "per row" : "per result set";
                            Console.WriteLine(
                                $"OK  '{flow.SysAlias}' is valid (translate: query -> {flow.Output.Path} " +
                                $"[{flow.Output.Mode}, one document {grain}]" +
                                (flow.Invoke is null ? ")." : $", then {flow.Invoke.Method} {flow.Invoke.Url})."));
                            return 0;
                        }

                        default:
                            throw new SqlFlowException("Unhandled document kind.");
                    }
                }

                case "plan":
                {
                    if (DocumentLoader.Load(documents, file, Console.Error.WriteLine) is not FileFlowDocument doc)
                    {
                        Console.Error.WriteLine("ERROR  'plan' supports file flows; ingestion, export, and stored-procedure work is determined at run time against the live source.");
                        return 1;
                    }

                    var plan = await runner.PlanAsync(doc.Flow).ConfigureAwait(false);
                    PrintPlan(plan);
                    return 0;
                }

                case "run":
                {
                    var json = args.Contains("--json");
                    var loaded = DocumentLoader.Load(documents, file, Console.Error.WriteLine);

                    // Ctrl+C aborts the in-flight run rather than killing the process: intercept it and trip the token
                    // the engine already threads into every SqlCommand, so the running statement is cancelled and its
                    // transaction rolled back, then report a clean interrupted exit (130, the conventional SIGINT code).
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cts.Cancel();
                    };

                    if (loaded is BatchFlowDocument batch)
                    {
                        return await RunBatchAsync(provider, batch.Document.Flow, file, args, json, cts.Token).ConfigureAwait(false);
                    }

                    RunParameters parameters;
                    try
                    {
                        parameters = ParseRunParameters(args);
                    }
                    catch (SqlFlowException ex)
                    {
                        Console.Error.WriteLine($"ERROR  {ex.Message}");
                        return 1;
                    }

                    // --health-check runs the document's embedded healthCheck: block (the derived hc sibling of
                    // an ingestion flow) instead of the load itself. Selection is by the derived flow's name, the
                    // same mechanism the node worker and a batch use, so all three entry points share one path.
                    string? selectedFlow = null;
                    if (args.Contains("--health-check"))
                    {
                        if (loaded is not IngestionFlowDocument { Document.HealthCheck: { } embeddedCheck })
                        {
                            Console.Error.WriteLine(
                                "ERROR  --health-check runs a flow's embedded health check, but this document declares none " +
                                "(a 'healthCheck:' block on a flowType: ing document).");
                            return 1;
                        }

                        selectedFlow = embeddedCheck.SysAlias;
                    }

                    var options = new DocumentExecutionOptions
                    {
                        LogLevel = ParseLogLevel(GetOption(args, "--log-level")),
                        Retrain = args.Contains("--retrain"),
                        ScmDryRun = args.Contains("--dry-run"),
                        ScmPush = !args.Contains("--no-push"),
                        Echo = json ? null : Console.WriteLine,
                        Parameters = parameters,
                        FlowName = selectedFlow,
                    };

                    DocumentExecutionResult exec;
                    try
                    {
                        exec = await provider.GetRequiredService<DocumentExecutor>()
                            .ExecuteAsync(loaded, file, options, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        Console.Error.WriteLine(
                            "CANCELLED  the run was interrupted (Ctrl+C); the in-flight statement was aborted and rolled back.");
                        return 130;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(exec.Result, ExecutionJson.Options));
                    }
                    else
                    {
                        PrintExecution(loaded, exec);
                    }

                    if (args.Contains("--show-sql") && exec.SqlTraceText.Length > 0)
                    {
                        Console.WriteLine(exec.SqlTraceText);
                    }

                    await RecordRunsInCatalogAsync(provider, args, json, file, [(file, exec.RunDirectory)]).ConfigureAwait(false);

                    return ExitCodeForExecution(loaded, exec, args);
                }

                case "infer":
                {
                    var request = provider.GetRequiredService<InferSpecLoader>().LoadFile(file);
                    var inference = provider.GetRequiredService<IInferenceService>();
                    // The table is already loaded, so validate by default: the report then carries the
                    // transform SELECT plus the per-column fit/silent-null status. --no-validate skips it.
                    var report = args.Contains("--no-validate")
                        ? await inference.InferAsync(request).ConfigureAwait(false)
                        : await inference.ValidateAsync(request).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(report, ExecutionJson.Options);

                    var outPath = GetOption(args, "--out", "-o");
                    if (outPath is not null)
                    {
                        await File.WriteAllTextAsync(outPath, json).ConfigureAwait(false);
                        Console.WriteLine($"Wrote inference report to {outPath}");
                    }
                    else
                    {
                        Console.WriteLine(json);
                    }

                    return 0;
                }

                case "discover":
                {
                    var flow = LoadFlow(loader, file);
                    var introspector = IntrospectorFor(provider, flow.Source.Type);
                    if (introspector is null)
                    {
                        Console.Error.WriteLine($"ERROR  'discover' supports JSON and XML sources; '{flow.Source.Type}' is not one.");
                        return 1;
                    }

                    var introspection = await introspector.IntrospectAsync(flow.Source,
                        ParseIntOption(args, 100, "--max-files"), ParseIntOption(args, 0, "--max-records"), ParseIntOption(args, 10, "--max-depth")).ConfigureAwait(false);
                    PrintDiscovery(flow.Name, introspection);
                    return 0;
                }

                case "paths":
                {
                    // 'paths' points straight at a JSON or XML file/folder (no pipeline YAML needed) and lists
                    // every addressable path in it.
                    var source = SourceFromPath(file, args);
                    var introspector = IntrospectorFor(provider, source.Type);
                    if (introspector is null)
                    {
                        Console.Error.WriteLine($"ERROR  'paths' supports JSON and XML; '{source.Type}' is not one.");
                        return 1;
                    }

                    var introspection = await introspector.IntrospectAsync(source,
                        ParseIntOption(args, 100, "--max-files"), ParseIntOption(args, 0, "--max-records"), ParseIntOption(args, 20, "--max-depth")).ConfigureAwait(false);
                    PrintInventory(introspection, args.Contains("--values"));
                    return 0;
                }

                case "flatten":
                {
                    // 'flatten' points straight at a JSON or XML file/folder. By default it emits the flatten
                    // "formula": a runnable flow stub with the resolved column schema (collisions fixed so it
                    // is lossless). With --data it instead dumps the flattened rows as CSV.
                    if (args.Contains("--data"))
                    {
                        var dataSource = SourceFromPath(file, args, withFlatten: true, suppressProvenance: !args.Contains("--provenance"));
                        var reader = provider.GetServices<ISourceReader>().FirstOrDefault(r => r.CanHandle(dataSource.Type));
                        if (reader is null)
                        {
                            Console.Error.WriteLine($"ERROR  no reader handles '{dataSource.Type}'.");
                            return 1;
                        }

                        await FlattenToCsvAsync(reader, dataSource, args).ConfigureAwait(false);
                        return 0;
                    }

                    var source = SourceFromPath(file, args, withFlatten: true);
                    var introspector = IntrospectorFor(provider, source.Type);
                    if (introspector is null)
                    {
                        Console.Error.WriteLine($"ERROR  'flatten' supports JSON and XML; '{source.Type}' is not one.");
                        return 1;
                    }

                    var introspection = await introspector.IntrospectAsync(source,
                        ParseIntOption(args, 100, "--max-files"), ParseIntOption(args, 0, "--max-records"), ParseIntOption(args, 20, "--max-depth")).ConfigureAwait(false);
                    await WriteFormulaAsync(source, introspection, args).ConfigureAwait(false);
                    return 0;
                }

                case "catalog":
                    return await RunCatalogAsync(provider, args).ConfigureAwait(false);

                case "detect-unique-key":
                    return await RunDetectUniqueKeyAsync(provider, args).ConfigureAwait(false);

                case "healthcheck":
                    return await RunAdHocHealthCheckAsync(provider, args).ConfigureAwait(false);

                case "lineage":
                {
                    // The graph-as-data subcommands query the control plane's synced view; anything else is
                    // the offline computation over a local flow folder. A folder named like a subcommand still
                    // resolves offline when it exists on disk (the concrete path wins over the keyword).
                    var lineageSub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
                    if (lineageSub is "objects" or "edges" or "waves" or "script" && !Directory.Exists(file))
                    {
                        return await RemoteVerbs.LineageRemoteAsync(positional, args).ConfigureAwait(false);
                    }

                    return await RunLineageAsync(provider, file, args).ConfigureAwait(false);
                }

                case "auth":
                    return await RunAuthCheckAsync(provider, args).ConfigureAwait(false);

                case "db":
                    return await RunDbAsync(provider, positional, args).ConfigureAwait(false);

                case "worker":
                    return await RunWorkerAsync(provider, args, verbose).ConfigureAwait(false);

                case "runs":
                {
                    // 'runs local' reads the on-disk artifacts (no catalog, no control plane); 'runs cancel'
                    // keeps its direct-catalog path as the break-glass route (it works when the control plane
                    // itself is down): used when --db is passed explicitly or when no control plane is
                    // configured. Everything else on 'runs' (list/show/trace, and cancel with a configured
                    // URL) goes through the control plane, the same API the GUI uses.
                    var runsSub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
                    if (runsSub == "local")
                    {
                        return await LocalInspectVerbs.ListLocalRunsAsync(positional, args).ConfigureAwait(false);
                    }

                    if (runsSub == "cancel" && (GetOption(args, "--db") is not null || !RemoteVerbs.IsConfigured(args)))
                    {
                        return await RunRunsAsync(provider, positional, args).ConfigureAwait(false);
                    }

                    return await RemoteVerbs.RunsAsync(positional, args).ConfigureAwait(false);
                }

                case "health":
                    return await RemoteVerbs.HealthAsync(args).ConfigureAwait(false);

                case "login":
                    return await RemoteVerbs.LoginAsync(args).ConfigureAwait(false);

                case "logout":
                    return await RemoteVerbs.LogoutAsync(args).ConfigureAwait(false);

                case "trigger":
                    return await RemoteVerbs.TriggerAsync(args).ConfigureAwait(false);

                case "groups":
                    return await RemoteVerbs.GroupsAsync(positional, args).ConfigureAwait(false);

                case "whoami":
                    return await RemoteVerbs.WhoAmIAsync(args).ConfigureAwait(false);

                case "doctor":
                    return await RemoteVerbs.DoctorAsync(args).ConfigureAwait(false);

                case "summary":
                    return await RemoteVerbs.SummaryAsync(args).ConfigureAwait(false);

                case "nodes":
                    return await RemoteVerbs.NodesAsync(args).ConfigureAwait(false);

                case "schedules":
                    return await RemoteVerbs.SchedulesAsync(positional, args).ConfigureAwait(false);

                case "repos":
                    return await RemoteVerbs.ReposAsync(positional, args).ConfigureAwait(false);

                case "pipelines":
                    return await RemoteVerbs.PipelinesAsync(positional, args).ConfigureAwait(false);

                case "datasources":
                    return await RemoteVerbs.DatasourcesAsync(positional, args).ConfigureAwait(false);

                case "search":
                    return await RemoteVerbs.SearchAsync(positional, args).ConfigureAwait(false);

                case "completions":
                    return CliCompletions.Print(positional);

                case "user":
                    return await RunUserAsync(provider, positional, args).ConfigureAwait(false);

                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Verifies Azure authentication end to end: 'sqlflow auth [--scope storage|keyvault|arm|&lt;uri&gt;]'. It reports
    /// the resolved mode (from SQLFLOW_AZURE_AUTH) and actually acquires a token for the chosen scope through the
    /// one credential factory every Azure path uses, so an operator can confirm service principal / managed
    /// identity / az-login works in THIS environment before running a cloud flow. Exit 0 on a token, 1 otherwise.
    /// </summary>
    private static async Task<int> RunAuthCheckAsync(IServiceProvider provider, string[] args)
    {
        var scopeArg = (GetOption(args, "--scope") ?? "storage").Trim();
        var scope = scopeArg.ToLowerInvariant() switch
        {
            "storage" or "adls" or "blob" => "https://storage.azure.com/.default",
            "keyvault" or "vault" => "https://vault.azure.net/.default",
            "arm" or "management" => "https://management.azure.com/.default",
            _ => scopeArg, // a verbatim scope URI
        };

        // Show both what the operator set and the mode it resolves to (normalized via the one resolver every Azure
        // path uses), plus whether managed identity is in play (the dev-PC vs in-Azure distinction).
        var raw = Environment.GetEnvironmentVariable("SQLFLOW_AZURE_AUTH");
        var resolved = AzureAuth.Mode();
        var resolvedLabel = resolved == CloudAuthMode.DefaultChain
            ? $"default chain ({(AzureEnvironment.IsRunningInAzure() ? "managed identity -> az CLI -> env" : "az CLI -> env; managed identity excluded off-cloud")})"
            : resolved.ToString();
        Console.WriteLine($"SQLFLOW_AZURE_AUTH: {(string.IsNullOrWhiteSpace(raw) ? "(unset)" : raw)} -> {resolvedLabel}");
        Console.WriteLine($"Acquiring a token for scope: {scope}");

        try
        {
            var credential = provider.GetRequiredService<IAzureCredentialFactory>().Create();
            var token = await credential.GetTokenAsync(new global::Azure.Core.TokenRequestContext([scope]), CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"OK   acquired a token (expires {token.ExpiresOn:u} UTC). Azure auth works.");
            return 0;
        }
        catch (Exception ex)
        {
            // A diagnostic verb: any failure to acquire a token is the answer the operator asked for, surfaced
            // with the underlying cause rather than a stack trace.
            Console.Error.WriteLine($"FAIL could not acquire a token: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Runs this host as a self-hosted compute node: <c>sqlflow worker --url &lt;control-plane&gt; [--token &lt;ref&gt;]
    /// [--db &lt;ref&gt;] [--pool a,b] [--poll-seconds N] [--drain-seconds N]</c>. The node speaks the node protocol
    /// to the control plane's dispatcher over HTTP (poll for work, report outcomes) with a bearer credential carrying
    /// the <c>node</c> scope, and executes each handed-out run through the same engine as a direct CLI run on THIS
    /// host, resolving every credential from its own environment. The node needs nothing but the control plane:
    /// each hand-out carries the run's definition, the snapshotted YAML, the lineage context and the live trace all
    /// travel over the same protocol, and no catalog connection is opened here. Any number of nodes may run at once:
    /// placement is the dispatcher's.
    /// </summary>
    private static async Task<int> RunWorkerAsync(IServiceProvider provider, string[] args, bool verbose)
    {
        Uri controlPlane;
        try
        {
            controlPlane = Remote.RemoteVerbs.RequireUrl(args);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        var resolver = provider.GetRequiredService<ISecretResolver>();
        var tokenReference = Remote.RemoteVerbs.ResolveToken(controlPlane, args);
        if (string.IsNullOrWhiteSpace(tokenReference))
        {
            Console.Error.WriteLine(
                "ERROR  no node credential: pass --token <ref> or set SQLFLOW_TOKEN to a personal access token minted with the " +
                "'node' scope (a ${env:...}/${keyvault:...} reference is resolved here on the node).");
            return 1;
        }

        string token;
        try
        {
            token = resolver.Resolve(tokenReference);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        // How long each poll asks the dispatcher to hold it when nothing is available (the node's heartbeat cadence
        // while saturated); the server caps it at the protocol maximum.
        var pollSeconds = Math.Clamp(ParseIntOption(args, 30, "--poll-seconds"), 1, SqlFlow.Dispatch.Protocol.NodeProtocol.MaxWaitSeconds);
        // How long a stopping node keeps finishing the runs it already holds before severing them. It must stay
        // under the orchestrator's termination grace period, or the platform's kill lands mid-drain and severs the
        // work anyway; zero severs at once.
        var drainSeconds = Math.Max(0, ParseIntOption(args, (int)RunWorker.DefaultDrainTimeout.TotalSeconds, "--drain-seconds"));
        // The pools this node serves (comma-separated). Empty means it takes only untargeted runs.
        var pools = (GetOption(args, "--pool") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A dedicated host for the node: the shared engine (so a worker run is byte-for-byte a CLI run), the HTTP
        // transport to the dispatcher (which is also where the run's definition, its lineage context and its live
        // trace travel, so the node opens no catalog connection), and the shared RunWorker loop. The
        // DocumentExecutor gets the stderr warning sink exactly as the CLI's own runs do (a later registration wins
        // over the engine's sink-less one).
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = true;
            });
            // Diagnostics on stderr, like every CLI path.
            builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });
        services.AddSqlFlowEngine();
        services.AddSingleton(sp => new DocumentExecutor(sp, Console.Error.WriteLine));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INodeTransport>(_ => new HttpNodeTransport(controlPlane, token));
        services.AddSingleton<RunWorker>();
        await using var workerProvider = services.BuildServiceProvider();

        var worker = workerProvider.GetRequiredService<RunWorker>();
        using var cts = new CancellationTokenSource();

        // Stop signals. SIGTERM is the one that matters in production: it is what an autoscaler reclaiming this
        // replica, a revision swap, or a `docker stop` sends, and .NET's DEFAULT handling of it terminates the
        // process immediately. That default is what turns a routine scale-in into lost work - every run this node
        // is executing dies mid-statement with no outcome reported, so each is recovered only by the dispatcher's
        // lease expiry, which consumes one of its execution attempts and repeats all of its work; a run caught by
        // three such stops is failed outright and blamed for dying. Handling the signal (Cancel = true suppresses
        // the default termination) hands control back here, where the worker stops taking work and drains what it
        // holds within the orchestrator's termination grace period. SIGINT is the interactive Ctrl+C and drains the
        // same way, rather than killing the process.
        var stopRequested = 0;
        void RequestStop(PosixSignalContext context)
        {
            // A SECOND signal means the sender is not willing to wait out the drain (an impatient operator, or an
            // orchestrator escalating). Leave Cancel false so the runtime terminates as it normally would: the
            // severed runs' leases lapse and the dispatcher requeues them, which is the honest outcome of refusing
            // the drain, and it keeps a worker from ever feeling unkillable.
            if (Interlocked.Exchange(ref stopRequested, 1) != 0)
            {
                return;
            }

            context.Cancel = true;
            cts.Cancel();
        }

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);

        var poolLabel = pools.Length > 0 ? string.Join(", ", pools) : "untargeted runs only";
        Console.WriteLine($"SQLFlow worker '{worker.NodeName}' polling {controlPlane} for work (poll {pollSeconds}s, pools: {poolLabel}, drain {drainSeconds}s). Press Ctrl+C to stop, again to stop without draining.");
        try
        {
            // An operator restart request (relayed by the dispatcher on a poll) trips the same token a stop signal
            // does: the loop stops taking work, drains its in-flight work, and returns, the process exits cleanly,
            // and the orchestrator recreates the replica.
            await worker.RunAsync(
                new RunWorkerOptions
                {
                    Pools = pools,
                    PollWait = TimeSpan.FromSeconds(pollSeconds),
                    DrainTimeout = TimeSpan.FromSeconds(drainSeconds),
                    OnRestartRequested = _ =>
                    {
                        cts.Cancel();
                        return Task.CompletedTask;
                    },
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A stop signal or an honored restart request: a clean stop.
        }

        Console.WriteLine("SQLFlow worker stopped.");
        return 0;
    }

    /// <summary>
    /// Operations against runs in the shadow catalog's durable queue: <c>sqlflow runs cancel &lt;runId&gt; [--db
    /// &lt;ref&gt;]</c>. Cancelling honors the run's lifecycle exactly as the control plane does (it shares
    /// <see cref="RunQueueStore.CancelAsync"/>): a still-queued run is dequeued outright; a run already executing has a
    /// durable cancel request stamped for its owning node to observe, abort the in-flight statement, and record
    /// cancelled. Like the <c>worker</c> and <c>db</c> verbs it talks to the catalog directly (no control-plane HTTP
    /// hop), so it works from any host that can reach the catalog database; the connection is a reference (default
    /// <c>${env:SQLFLOW_CATALOG_DB}</c>), never an embedded secret.
    /// </summary>
    private static async Task<int> RunRunsAsync(IServiceProvider provider, string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (sub != "cancel")
        {
            Console.Error.WriteLine("ERROR  'runs' supports: cancel <runId>. Usage: sqlflow runs cancel <runId> [--db <conn-ref>]");
            return 1;
        }

        if (positional.Length < 3 || !Guid.TryParse(positional[2], out var runId))
        {
            Console.Error.WriteLine("ERROR  'runs cancel' requires a run id: sqlflow runs cancel <runId> [--db <conn-ref>]");
            return 1;
        }

        var reference = GetOption(args, "--db") ?? "${env:SQLFLOW_CATALOG_DB}";
        if (SecretHygiene.LooksLikeEmbeddedSecret(reference))
        {
            Console.Error.WriteLine(
                "WARN  --db embeds a credential on the command line (it lands in shell history). Prefer the canonical " +
                "${env:SQLFLOW_CATALOG_DB}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, with local " +
                "values in the git-ignored .sqlflow/env file.");
        }

        string connectionString;
        try
        {
            connectionString = provider.GetRequiredService<ISecretResolver>().Resolve(reference);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        // EF Core throws SqlException / InvalidOperationException (not SqlFlowException) on a bad connection or
        // permission; catch them here so the verb reports a clean, redacted message instead of a stack trace.
        try
        {
            await using var db = CatalogDatabase.Create(connectionString);
            var outcome = await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow).ConfigureAwait(false);
            switch (outcome)
            {
                case CancelOutcome.Cancelled:
                    Console.WriteLine($"OK   run {runId} was queued and is now cancelled.");
                    return 0;
                case CancelOutcome.CancelRequested:
                    Console.WriteLine(
                        $"OK   run {runId} is running; cancellation requested. Its node will abort the in-flight statement " +
                        "and record it cancelled.");
                    return 0;
                case CancelOutcome.NotFound:
                    Console.Error.WriteLine($"ERROR  no run '{runId}'.");
                    return 1;
                default:
                    Console.Error.WriteLine($"ERROR  run '{runId}' has already finished and cannot be cancelled.");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not SqlFlowException)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Local-user administration straight against the shadow catalog: <c>sqlflow user reset-password &lt;username&gt;
    /// [--db &lt;ref&gt;]</c>. Like <c>db</c> and <c>runs cancel</c>, it talks to the catalog directly (no
    /// control-plane HTTP hop, no bearer token), so it works from any host that can reach the catalog database and
    /// needs only database access, not a running control plane. That makes it the recovery path when no admin can
    /// sign in, and it removes the reason to keep a break-glass bootstrap secret enabled: the new password is read
    /// from a hidden interactive prompt (never a flag, so it stays out of shell history) and hashed with the exact
    /// same hasher the control plane verifies against. The connection is a reference (default
    /// <c>${env:SQLFLOW_CATALOG_DB}</c>), never an embedded secret.
    /// </summary>
    private static async Task<int> RunUserAsync(IServiceProvider provider, string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (sub != "reset-password")
        {
            Console.Error.WriteLine(
                "ERROR  'user' supports: reset-password <username>. Usage: sqlflow user reset-password <username> [--db <conn-ref>]");
            return 1;
        }

        if (positional.Length < 3 || string.IsNullOrWhiteSpace(positional[2]))
        {
            Console.Error.WriteLine("ERROR  'user reset-password' requires a username: sqlflow user reset-password <username> [--db <conn-ref>]");
            return 1;
        }

        var username = positional[2];
        var reference = GetOption(args, "--db") ?? "${env:SQLFLOW_CATALOG_DB}";
        if (SecretHygiene.LooksLikeEmbeddedSecret(reference))
        {
            Console.Error.WriteLine(
                "WARN  --db embeds a credential on the command line (it lands in shell history). Prefer the canonical " +
                "${env:SQLFLOW_CATALOG_DB}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, with local " +
                "values in the git-ignored .sqlflow/env file.");
        }

        string connectionString;
        try
        {
            connectionString = provider.GetRequiredService<ISecretResolver>().Resolve(reference);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        // EF Core throws SqlException / InvalidOperationException (not SqlFlowException) on a bad connection or
        // permission; catch them here so the verb reports a clean, redacted message instead of a stack trace.
        try
        {
            await using var db = CatalogDatabase.Create(connectionString);
            var user = await UserStore.FindByUsernameAsync(db, username).ConfigureAwait(false);
            if (user is null)
            {
                Console.Error.WriteLine($"ERROR  no user '{username}' in the catalog.");
                return 1;
            }

            // Only local users hold a password here; an SSO (Entra) user's credential lives in the external
            // provider, so refuse before prompting rather than after. (UserStore.SetPasswordHashAsync enforces the
            // same rule as a backstop.)
            if (user.Provider != UserProviders.Local)
            {
                Console.Error.WriteLine(
                    $"ERROR  user '{user.Username}' signs in through '{user.Provider}', not a local password; reset it in that provider.");
                return 1;
            }

            if (!user.Active)
            {
                Console.Error.WriteLine(
                    $"NOTE  user '{user.Username}' is currently deactivated; the reset succeeds but they cannot sign in until reactivated.");
            }

            var password = PromptForNewPassword();
            if (password is null)
            {
                // The prompt already explained why (mismatch, too short, or empty); treat as a clean abort.
                return 1;
            }

            var hash = LocalPasswords.Hash(user, password);
            var nowUtc = DateTime.UtcNow;
            var outcome = await UserStore.SetPasswordHashAsync(db, user.Id, hash, nowUtc).ConfigureAwait(false);
            switch (outcome)
            {
                case UserMutation.Applied:
                    Console.WriteLine($"OK   password reset for '{user.Username}' ({user.Role}).");
                    // A break-glass credential change is exactly what enterprise policy expects audited. There is no
                    // catalog audit table (the API's own reset writes none either), so emit a structured, secret-free
                    // record of who/what/when/where to the console the operator's session capture (PAM/bastion) logs.
                    // DescribeTarget is secret-free (server + database only); the password never appears anywhere.
                    Console.WriteLine(
                        $"     audit: '{user.Username}' ({user.Role}) reset by {Environment.UserName}@{Environment.MachineName} " +
                        $"on {CatalogDatabase.DescribeTarget(connectionString)} at {nowUtc.ToString("O", CultureInfo.InvariantCulture)}.");
                    return 0;
                case UserMutation.NotFound:
                    // The row vanished between lookup and update (an admin deleted the account through the API, or
                    // it was removed in the database directly); report it plainly.
                    Console.Error.WriteLine($"ERROR  user '{user.Username}' no longer exists.");
                    return 1;
                case UserMutation.NotLocal:
                    Console.Error.WriteLine($"ERROR  user '{user.Username}' is not a local-password account; nothing was changed.");
                    return 1;
                default:
                    Console.Error.WriteLine($"ERROR  password reset for '{user.Username}' did not apply.");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SqlFlowException)
        {
            Console.Error.WriteLine($"ERROR  user 'reset-password' failed: {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Reads a new local password without echoing it, then confirms it, returning null (after printing why) on any
    /// rejection so the caller aborts cleanly. When stdin is redirected (piped automation) a single line is read
    /// instead: there is no terminal to mask or to confirm against, so the one line is taken as-is and still
    /// length-checked. The value never touches a command-line flag, so it stays out of shell history either way.
    /// </summary>
    private static string? PromptForNewPassword()
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.ReadLine();
            if (string.IsNullOrEmpty(piped) || piped.Length < LocalPasswords.MinLength)
            {
                Console.Error.WriteLine($"ERROR  the password must be at least {LocalPasswords.MinLength} characters.");
                return null;
            }

            return piped;
        }

        Console.Write("New password: ");
        var password = CliConsole.ReadHiddenLine();
        if (password.Length < LocalPasswords.MinLength)
        {
            Console.Error.WriteLine($"ERROR  the password must be at least {LocalPasswords.MinLength} characters.");
            return null;
        }

        Console.Write("Confirm password: ");
        var confirm = CliConsole.ReadHiddenLine();
        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("ERROR  the two entries did not match; nothing was changed.");
            return null;
        }

        return password;
    }

    /// <summary>
    /// The shadow-catalog (database mode) operations: <c>sqlflow db migrate|sync|status [path] [--db &lt;ref&gt;]</c>.
    /// The catalog is an EF-managed read-model of the git/YAML estate and the on-disk run history; git stays the
    /// source of truth. <c>migrate</c> upgrades an existing catalog to the current schema version, and with
    /// <c>--create</c> provisions a new one (create the database, or initialise the catalog in an empty database);
    /// without <c>--create</c> a missing or non-catalog database is refused. <c>sync</c> projects the estate +
    /// run.json artifacts into an existing catalog (upgrading it first); <c>status</c> reports applied vs pending
    /// migrations. The connection is a reference (default <c>${env:SQLFLOW_CATALOG_DB}</c>), never an embedded secret.
    /// </summary>
    private static async Task<int> RunDbAsync(IServiceProvider provider, string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        var reference = GetOption(args, "--db") ?? "${env:SQLFLOW_CATALOG_DB}";
        if (SecretHygiene.LooksLikeEmbeddedSecret(reference))
        {
            Console.Error.WriteLine(
                "WARN  --db embeds a credential on the command line (it lands in shell history). Prefer the canonical " +
                "${env:SQLFLOW_CATALOG_DB}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, with local " +
                "values in the git-ignored .sqlflow/env file.");
        }

        string connectionString;
        try
        {
            connectionString = provider.GetRequiredService<ISecretResolver>().Resolve(reference);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        // The catalog operations talk to SQL Server through EF Core, which throws SqlException / DbUpdateException
        // / InvalidOperationException (none are SqlFlowException) on a bad connection, missing permission, or a
        // failed migration. Catch them here so the verb reports a clean, redacted message instead of crashing.
        try
        {
            switch (sub)
            {
                case "migrate":
                {
                    // Creating the database (or initialising the catalog in an empty one) requires the explicit
                    // --create flag, so a mistyped --db can never silently provision the wrong database. Without
                    // it, migrate upgrades an EXISTING catalog only and refuses anything else.
                    if (args.Contains("--create"))
                    {
                        await CatalogDatabase.MigrateAsync(connectionString).ConfigureAwait(false);
                    }
                    else
                    {
                        await CatalogDatabase.MigrateExistingAsync(connectionString).ConfigureAwait(false);
                    }

                    var (applied, pending) = await CatalogDatabase.StatusAsync(connectionString).ConfigureAwait(false);
                    Console.WriteLine(
                        $"OK   catalog database current at '{(applied.Count > 0 ? applied[^1] : "(none)")}' " +
                        $"({applied.Count} migration(s) applied, {pending.Count} pending).");
                    return 0;
                }

                case "status":
                {
                    var (applied, pending) = await CatalogDatabase.StatusAsync(connectionString).ConfigureAwait(false);
                    Console.WriteLine($"catalog: {applied.Count} migration(s) applied, {pending.Count} pending.");
                    foreach (var migration in pending)
                    {
                        Console.WriteLine($"  pending: {migration}");
                    }

                    return pending.Count == 0 ? 0 : 2;
                }

                case "sync":
                {
                    var directory = positional.Length > 2 ? positional[2] : Directory.GetCurrentDirectory();
                    var repoName = GetOption(args, "--repo") is { Length: > 0 } r
                        ? r
                        : (new DirectoryInfo(Path.GetFullPath(directory)).Name is { Length: > 0 } folder ? folder : "default");
                    var repoUrl = GetOption(args, "--repo-url");
                    // --connect adds the derived lineage tier: object metadata is fetched from the live database
                    // (catalog + sys.sql_modules), which links flows across repos through the shared objects.
                    var connect = args.Contains("--connect");
                    // Upgrade an existing catalog's schema first, so a sync just works. Never creates: point at an
                    // existing catalog, or provision one with 'sqlflow db migrate --create'.
                    await CatalogDatabase.MigrateExistingAsync(connectionString).ConfigureAwait(false);
                    await using var context = CatalogDatabase.Create(connectionString);
                    // The same under-the-hood lines the managed sync streams into its trace (tier begins, each
                    // server's connect / harvest tally / failure), printed as they happen. Server collection is
                    // parallel, so console writes serialize through one lock.
                    var progressLock = new object();
                    Task PrintProgress(string message, CancellationToken _)
                    {
                        lock (progressLock)
                        {
                            Console.WriteLine($"     lineage: {message}");
                        }

                        return Task.CompletedTask;
                    }

                    var result = await new CatalogSync().SyncAsync(
                        context, directory, repoName, repoUrl, DateTime.UtcNow,
                        includeDerived: connect, secrets: provider.GetRequiredService<ISecretResolver>(),
                        lineageProgress: PrintProgress).ConfigureAwait(false);
                    Console.WriteLine(
                        $"OK   synced '{directory}': pipelines +{result.PipelinesAdded} added, {result.PipelinesUpdated} updated, " +
                        $"{result.PipelinesUnchanged} unchanged, {result.PipelinesDeactivated} deactivated, {result.PipelinesDeleted} removed; runs +{result.RunsAdded} added " +
                        $"({result.RunFilesAdded} files, {result.RunAssertionsAdded} assertions, {result.RunStatementsAdded} statements, " +
                        $"{result.RunEventsAdded} events, {result.RunSurrogateKeysAdded} surrogate-keys, {result.RunHealthCheckMetricsAdded} hc-metrics), " +
                        $"{result.RunsSkipped} known, {result.RunsFailed} unreadable; " +
                        $"lineage {result.ObjectsUpserted} objects, {result.ObjectColumns} columns, {result.LineageEdges} edges, " +
                        $"{result.Waves} waves, {result.FlowDependencies} dependencies" +
                        $"{(result.LineageEdgesPreserved > 0 ? $", {result.LineageEdgesPreserved} previously-derived edge(s) preserved" : "")}" +
                        $"{(result.ObjectsSuperseded > 0 ? $", {result.ObjectsSuperseded} superseded keys healed" : "")}" +
                        $"{(result.LineageConnected ? " (connected)" : "")}.");
                    foreach (var warning in result.Warnings.Take(20))
                    {
                        Console.Error.WriteLine($"WARN  {warning}");
                    }

                    return 0;
                }

                default:
                    Console.Error.WriteLine("Usage: sqlflow db <migrate|sync|status> [path] [--db <conn-ref>] [--create]");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"ERROR  catalog '{sub}' failed: {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// The self-maintaining shadow: after a flow run, when a catalog database is configured (<c>--db</c> or
    /// <c>SQLFLOW_CATALOG_DB</c>), record the produced run(s) and their pipeline(s) into it automatically so
    /// database mode stays current without a manual <c>db sync</c>. Opt out with <c>--no-db-sync</c>. Best-effort:
    /// a write-back failure is reported as a warning and never changes the run's own exit code. Lineage and
    /// execution waves are NOT recomputed here (that remains <c>db sync</c>'s job); this records the pipeline row
    /// and each run with its drill-down detail. The repo is <c>--repo</c> / <c>SQLFLOW_REPO</c> / the primary
    /// flow's folder name. A direct run passes one entry; a batch passes the batch document plus each member.
    /// </summary>
    private static async Task RecordRunsInCatalogAsync(
        IServiceProvider provider, string[] args, bool json, string primaryFlowFile,
        IReadOnlyList<(string FlowFile, string? RunDirectory)> runs)
    {
        if (args.Contains("--no-db-sync"))
        {
            return;
        }

        // In play ONLY when --db is passed or SQLFLOW_CATALOG_DB is set; otherwise the run is a pure file/YAML
        // operation and the catalog is simply absent (no error, no output).
        var reference = GetOption(args, "--db")
            ?? (Environment.GetEnvironmentVariable("SQLFLOW_CATALOG_DB") is { Length: > 0 } ? "${env:SQLFLOW_CATALOG_DB}" : null);
        if (reference is null)
        {
            return;
        }

        // Everything below (including path resolution) is inside the catch, so a write-back can never affect the
        // run's own outcome.
        try
        {
            var candidates = runs
                .Where(r => r.RunDirectory is not null)
                .Select(r => (r.FlowFile, RunJson: Path.Combine(r.RunDirectory!, "run.json")))
                .Where(r => File.Exists(r.RunJson))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var repoName = GetOption(args, "--repo")
                ?? (Environment.GetEnvironmentVariable("SQLFLOW_REPO") is { Length: > 0 } configured
                    ? configured
                    : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(primaryFlowFile)) ?? ".").Name is { Length: > 0 } folder
                        ? folder
                        : "default");
            var repoUrl = GetOption(args, "--repo-url");

            var connectionString = provider.GetRequiredService<ISecretResolver>().Resolve(reference);
            // Upgrade an existing catalog's schema once, so a configured run's write-back just works. Never
            // creates: a mistyped SQLFLOW_CATALOG_DB must not silently conjure a catalog during a run. (This whole
            // block is best-effort; a refusal surfaces as a warning and never changes the run's exit code.)
            await CatalogDatabase.MigrateExistingAsync(connectionString).ConfigureAwait(false);

            var sync = new CatalogSync();
            var recorded = 0;
            var warnings = new List<string>();
            foreach (var (flowFile, runJson) in candidates)
            {
                // A fresh context per run keeps the change tracker clean across a batch's many members.
                await using var context = CatalogDatabase.Create(connectionString);
                var result = await sync.RecordRunAsync(context, flowFile, runJson, repoName, repoUrl, DateTime.UtcNow).ConfigureAwait(false);
                if (result.RunRecorded)
                {
                    recorded++;
                }

                warnings.AddRange(result.Warnings);
            }

            if (!json)
            {
                Console.WriteLine($"  catalog: {recorded} of {candidates.Count} run(s) recorded into [{repoName}].");
            }

            foreach (var warning in warnings.Take(10))
            {
                Console.Error.WriteLine($"WARN  {warning}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"WARN  catalog write-back skipped ({SecretHygiene.RedactedMessage(ex)}); the run itself is unaffected. Run 'sqlflow db sync' to backfill.");
        }
    }

    /// <summary>
    /// The zero-configuration health check: 'sqlflow healthcheck --source &lt;ref&gt; --object schema.table'.
    /// No pipeline file, no registration, no SQLFlow adoption required: the date column is auto-detected from
    /// the catalog (pin --date-column to override), the metric defaults to COUNT(*), and models stay in
    /// memory unless --state-dir persists them between runs. The exact same runner as flow documents, so the
    /// detection stack (trend, AutoML calendar, ESD, PELT, maturity, quality probes) is identical.
    /// </summary>
    private static async Task<int> RunAdHocHealthCheckAsync(IServiceProvider provider, string[] args)
    {
        // The canonical default: SQLFLOW_SOURCE. Zero flags against the usual estate; the resolver's error
        // names the variable when it is not set.
        var source = GetOption(args, "--source") ?? "${env:SQLFLOW_SOURCE}";
        if (SecretHygiene.LooksLikeEmbeddedSecret(source))
        {
            Console.Error.WriteLine(
                "WARN  --source embeds a credential on the command line (it lands in shell history). Prefer the " +
                "canonical ${env:SQLFLOW_SOURCE}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, " +
                "with local values in the git-ignored .sqlflow/env file.");
        }

        var kind = ParseProviderOption(GetOption(args, "--provider"));
        if (kind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
        {
            Console.Error.WriteLine("ERROR  healthcheck runs T-SQL on the monitored table; --provider must be mssql or azdb.");
            return 1;
        }

        var objectName = RequireObject(args);
        var json = args.Contains("--json");
        void Note(string message)
        {
            // Keep stdout clean JSON when --json is set; the notes still reach the operator on stderr.
            if (json)
            {
                Console.Error.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }

        // A two-part object resolves its database from the connection's default catalog.
        var database = objectName.Database;
        if (database is null)
        {
            var resolved = await provider.GetRequiredService<IConnectionResolver>()
                .ResolveAsync(source, ConnectionRole.Target, kind).ConfigureAwait(false);
            database = await ScalarStringAsync(resolved.CanonicalString, "SELECT DB_NAME();").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(database))
            {
                Console.Error.WriteLine("ERROR  the connection has no default database; use a three-part --object database.schema.table.");
                return 1;
            }
        }

        // The date column: pinned, or auto-detected from the catalog with the reasoning shown.
        var dateColumn = GetOption(args, "--date-column");
        if (dateColumn is null)
        {
            var catalog = provider.GetRequiredService<CatalogService>();
            var obj = await catalog.IntrospectAsync(source, objectName, kind).ConfigureAwait(false);
            if (obj is null)
            {
                Console.Error.WriteLine($"ERROR  object {objectName.QualifiedName} was not found.");
                return 1;
            }

            var choice = DateColumnSelector.Choose(obj.Columns.Select(c => (c.Name, c.NativeType)).ToList());
            if (choice.Column is null)
            {
                Console.Error.WriteLine($"ERROR  could not auto-detect a date column: {choice.Reasoning}. Pass --date-column <name>.");
                return 1;
            }

            dateColumn = choice.Column;
            Note($"date column: {choice.Reasoning}");
        }

        var expression = GetOption(args, "--base-value") ?? "COUNT(*)";
        var stateDir = GetOption(args, "--state-dir");
        var flowName = $"{database}.{objectName.Schema}.{objectName.Name}";
        var flow = new HealthCheckFlow
        {
            FlowId = BitConverter.ToInt32(FlowIdentity.FromName(flowName).ToByteArray(), 0) & 0x7FFFFFFF,
            SysAlias = flowName,
            Server = "adhoc",
            Target = RelationalObject.Parse($"[{database}].[{objectName.Schema}].[{objectName.Name}]"),
            DateColumn = dateColumn,
            Metrics = [new HealthCheckMetric { Name = HealthCheckMetric.DefaultName(expression), Expression = expression }],
            FilterCriteria = GetOption(args, "--filter"),
            // An ephemeral run retrains every time, so the default budget stays snappy; a persisted state
            // dir gets the full document-mode budget because the model is reused afterwards.
            MaxExperimentSeconds = ParseIntOption(args, stateDir is null ? 30 : 120, "--budget"),
            AnomalyThreshold = ParseDoubleOption(args, 2.0, "--threshold"),
            EsdAlpha = ParseDoubleOption(args, 0.025, "--alpha"),
            MaturityDays = ParseIntOption(args, 1, "--maturity"),
            Training = args.Contains("--retrain") ? HealthCheckTraining.Always : HealthCheckTraining.Auto,
        };

        IHealthCheckModelStore store = stateDir is null
            ? new EphemeralHealthCheckModelStore()
            : new HealthCheckModelStore(Path.GetFullPath(stateDir));
        var runner = WithoutDatabaseHealthCheck.BuildRunner(
            [
                new DataSource
                {
                    Alias = "adhoc",
                    Kind = kind ?? DataSourceKind.MSSQL,
                    ConnectionRef = source,
                    Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
                },
            ],
            store,
            provider.GetRequiredService<ISecretResolver>());

        var runLogger = new RunLogger(ParseLogLevel(GetOption(args, "--log-level")), echo: json ? null : Console.WriteLine);
        var outcome = await runner
            .RunAsync(flow, new IngestionRunOptions { ExecMode = "cli-adhoc", Events = runLogger })
            .ConfigureAwait(false);
        var result = outcome.Result;

        // With a state dir the run leaves the canonical artifact set there, exactly like a flow document.
        if (stateDir is not null)
        {
            var runDirectory = RunHistory.WriteAt(Path.GetFullPath(stateDir), flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["run.json"] = JsonSerializer.Serialize(new RunArtifact
                {
                    FlowKind = "hc",
                    FlowName = flowName,
                    RunId = result.RunId,
                    Success = result.Success,
                    WrittenUtc = DateTime.UtcNow,
                    Error = result.Error,
                    Result = result,
                }, ExecutionJson.Options),
                ["run.log"] = runLogger.Render(),
                ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
                ["healthcheck.json"] = outcome.Report is null ? string.Empty : JsonSerializer.Serialize(outcome.Report, ExecutionJson.Options),
            }, Console.Error.WriteLine);
            if (runDirectory is not null && !json)
            {
                Console.WriteLine($"  run log: {runDirectory}");
            }
        }

        if (GetOption(args, "--out", "-o") is { } outPath)
        {
            await File.WriteAllTextAsync(outPath,
                outcome.Report is null ? string.Empty : JsonSerializer.Serialize(outcome.Report, ExecutionJson.Options)).ConfigureAwait(false);
            Note($"Wrote the scored report to {outPath}");
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(outcome, ExecutionJson.Options));
        }
        else
        {
            PrintHealthCheckResult(result, outcome.Report);
        }

        if (args.Contains("--show-sql"))
        {
            Console.WriteLine(SqlTrace.Render(result.SqlTrace));
        }

        return HealthCheckExitCode(result, args.Contains("--fail-on-anomaly"));
    }

    /// <summary>
    /// 'sqlflow lineage &lt;folder&gt;': the three-phase lineage computation over a flow estate. Offline by
    /// default (declared documents + observed run artifacts); --connect adds the derived tier (catalog and
    /// module expansion). The canonical lineage.json lands under the folder's .sqlflow/lineage/; --of walks
    /// impact (--down, default) or dependencies (--up) from an object or flow. --strict exits 2 on cycles.
    /// </summary>
    private static async Task<int> RunLineageAsync(IServiceProvider provider, string folder, string[] args)
    {
        var computation = await LineageService.ComputeDetailedAsync(new LineageOptions
        {
            FlowDirectory = folder,
            IncludeObserved = !args.Contains("--no-observed"),
            IncludeDerived = args.Contains("--connect"),
            Secrets = provider.GetRequiredService<ISecretResolver>(),
        }).ConfigureAwait(false);
        var report = computation.Report;

        // The canonical artifact: always written, like every other run product; the summary only points at
        // it when the write actually succeeded. With --dump-facts the raw pre-merge facts land beside it.
        string? artifactPath = null;
        string? factsPath = null;
        try
        {
            var lineageDirectory = Path.Combine(Path.GetFullPath(folder), ".sqlflow", "lineage");
            Directory.CreateDirectory(lineageDirectory);
            var candidate = Path.Combine(lineageDirectory, "lineage.json");
            await File.WriteAllTextAsync(candidate, JsonSerializer.Serialize(report, ExecutionJson.Options)).ConfigureAwait(false);
            artifactPath = candidate;

            if (args.Contains("--dump-facts"))
            {
                var factsCandidate = Path.Combine(lineageDirectory, "facts.json");
                await File.WriteAllTextAsync(factsCandidate, JsonSerializer.Serialize(computation.Facts, ExecutionJson.Options)).ConfigureAwait(false);
                factsPath = factsCandidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"WARN  could not write lineage artifacts: {ex.Message}");
        }

        if (GetOption(args, "--out", "-o") is { } outPath)
        {
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, ExecutionJson.Options)).ConfigureAwait(false);
        }

        var json = args.Contains("--json");
        if (json && GetOption(args, "--of") is null && GetOption(args, "--explain") is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, ExecutionJson.Options));
            return LineageExitCode(report, args.Contains("--strict"));
        }

        if (!json)
        {
            Console.WriteLine(
                $"Lineage over {report.Flows.Count} flow(s), {report.Objects.Count} object(s), {report.Edges.Count} edge(s) " +
                $"({string.Join("+", report.TiersUsed).ToLowerInvariant()})");
        }

        if (GetOption(args, "--explain") is { } explainFlow)
        {
            var explanation = LineageService.ExplainFlow(report, explainFlow);
            if (explanation is null)
            {
                Console.Error.WriteLine($"ERROR  '{explainFlow}' is not a flow in the graph (use --of for an object's impact walk).");
                return 1;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(explanation, ExecutionJson.Options));
            }
            else
            {
                PrintExplanation(explanation);
                if (factsPath is not null)
                {
                    Console.WriteLine($"  facts: {factsPath}");
                }
            }

            return LineageExitCode(report, args.Contains("--strict"));
        }

        if (GetOption(args, "--of") is { } subject)
        {
            var resolved = LineageService.ResolveSubject(report, subject);
            if (resolved.Count == 0)
            {
                Console.Error.WriteLine($"ERROR  '{subject}' matches nothing in the graph.");
                return 1;
            }

            if (resolved.Count > 1)
            {
                Console.Error.WriteLine($"ERROR  '{subject}' is ambiguous: {string.Join(", ", resolved)}. Use the full key.");
                return 1;
            }

            // Direction is downstream (impact) by default; --down states that default explicitly and --up flips
            // it. Both at once is a contradiction, rejected rather than silently preferring one.
            var up = args.Contains("--up");
            if (up && args.Contains("--down"))
            {
                Console.Error.WriteLine("ERROR  choose either --up (dependencies) or --down (impact), not both.");
                return 1;
            }

            var reached = up ? LineageService.Upstream(report, resolved[0]) : LineageService.Downstream(report, resolved[0]);
            if (json)
            {
                // Machine-readable impact walk: the artifact already carries the full report.
                Console.WriteLine(JsonSerializer.Serialize(
                    new { Subject = resolved[0], Direction = up ? "upstream" : "downstream", Nodes = reached }, ExecutionJson.Options));
                return LineageExitCode(report, args.Contains("--strict"));
            }

            Console.WriteLine($"  {(up ? "upstream of" : "downstream of")} {resolved[0]}: {reached.Count} node(s)");
            foreach (var node in reached)
            {
                Console.WriteLine($"    {node}");
            }
        }
        else
        {
            foreach (var wave in report.ExecutionPlan.Waves)
            {
                var marker = report.ExecutionPlan.Unordered.Count > 0 && wave.Wave == report.ExecutionPlan.Waves.Count
                             && wave.Flows.All(report.ExecutionPlan.Unordered.Contains)
                    ? " (fallback: unresolved order)"
                    : string.Empty;
                Console.WriteLine($"  wave {wave.Wave}{marker}: {string.Join(", ", wave.Flows)}");
            }

            foreach (var cycle in report.Cycles)
            {
                Console.WriteLine($"  CYCLE  {string.Join(" -> ", cycle.Flows)} via {string.Join(", ", cycle.ViaObjects)}");
            }
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"  WARN  {warning}");
        }

        if (artifactPath is not null)
        {
            Console.WriteLine($"  lineage: {artifactPath}");
        }

        if (factsPath is not null)
        {
            Console.WriteLine($"  facts: {factsPath}");
        }

        return LineageExitCode(report, args.Contains("--strict"));
    }

    /// <summary>Prints the traced rationale for one flow's wave placement: its dependencies (with the mediating
    /// objects and their waves), what it reads and writes by tier, and the flows that depend on it.</summary>
    private static void PrintExplanation(FlowExplanation x)
    {
        Console.WriteLine($"  flow '{x.Flow}' ({x.Kind}): wave {x.Wave}{(x.InCycle ? " (in a dependency cycle)" : string.Empty)}");

        if (x.DependsOn.Count == 0)
        {
            Console.WriteLine("    depends on: (nothing in the set; a wave-1 root)");
        }
        else
        {
            Console.WriteLine("    depends on:");
            foreach (var dependency in x.DependsOn)
            {
                Console.WriteLine($"      {dependency.Flow} (wave {dependency.Wave}) via {string.Join(", ", dependency.ViaObjects)}");
            }
        }

        foreach (var edge in x.Reads)
        {
            Console.WriteLine($"    reads   [{edge.Tier.ToString().ToLowerInvariant()}] {edge.ObjectName}{ObservedSuffix(edge)}");
        }

        foreach (var edge in x.Writes)
        {
            Console.WriteLine($"    {edge.Relation.ToString().ToLowerInvariant(),-7} [{edge.Tier.ToString().ToLowerInvariant()}] {edge.ObjectName}{ObservedSuffix(edge)}");
        }

        if (x.RequiredBy.Count > 0)
        {
            Console.WriteLine($"    required by: {string.Join(", ", x.RequiredBy.Select(d => $"{d.Flow} (wave {d.Wave})"))}");
        }

        static string ObservedSuffix(ExplainEdge edge)
            => edge.ObservedRunId is { } runId ? $"  (run {runId.ToString("N")[..8]}{(edge.Step is null ? string.Empty : $", {edge.Step}")})" : string.Empty;
    }

    private static int LineageExitCode(Core.Lineage.LineageReport report, bool strict)
        => strict && report.Cycles.Count > 0 ? 2 : 0;

    private static async Task<string?> ScalarStringAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
    }

    private static double ParseDoubleOption(string[] args, double fallback, params string[] names)
    {
        var value = GetOption(args, names);
        return value is not null
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
               && double.IsFinite(parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static async Task<int> RunCatalogAsync(IServiceProvider provider, string[] args)
    {
        var positional = PositionalArguments(args);
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;

        var source = GetOption(args, "--source");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("ERROR  catalog commands require --source <@alias | ${ref} | connection string>.");
            return 1;
        }

        var service = provider.GetRequiredService<CatalogService>();
        var json = args.Contains("--json");
        var database = GetOption(args, "--database");
        var kind = ParseProviderOption(GetOption(args, "--provider"));
        var query = new CatalogQuery
        {
            NameLike = GetOption(args, "--like"),
            IncludeSystem = args.Contains("--system"),
            IncludeViews = !args.Contains("--no-views"),
            IncludeTables = !args.Contains("--no-tables"),
            Offset = ParseIntOption(args, 0, "--offset"),
            Limit = ParseIntOption(args, 200, "--limit"),
        };

        switch (sub)
        {
            case "databases":
                Output(json, await service.DatabasesAsync(source, query, kind).ConfigureAwait(false),
                    d => d.Collation is null ? d.Name : $"{d.Name}  ({d.Collation})");
                return 0;

            case "schemas":
                Output(json, await service.SchemasAsync(source, database, query, kind).ConfigureAwait(false), s => s.Name);
                return 0;

            case "tables":
            {
                var page = await service.ObjectsAsync(source, new ObjectScope { Database = database, Schema = GetOption(args, "--schema") }, query, kind).ConfigureAwait(false);
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(page, ExecutionJson.Options));
                }
                else
                {
                    foreach (var o in page.Items)
                    {
                        Console.WriteLine($"{o.Schema}.{o.Name}  {o.Type}  ~{o.ApproxRows} row(s)");
                    }

                    Console.WriteLine($"({page.Items.Count} of {page.Total})");
                }

                return 0;
            }

            case "search":
            {
                var term = GetOption(args, "--term");
                if (string.IsNullOrWhiteSpace(term))
                {
                    Console.Error.WriteLine("ERROR  catalog search requires --term <text>.");
                    return 1;
                }

                Output(json, await service.SearchAsync(source, database, term, kind).ConfigureAwait(false), m => $"{m.Schema}.{m.Name}  ({m.Type})");
                return 0;
            }

            case "columns":
            {
                var name = RequireObject(args);
                var obj = await service.IntrospectAsync(source, name, kind).ConfigureAwait(false);
                if (obj is null)
                {
                    Console.Error.WriteLine($"ERROR  object {name.QualifiedName} was not found.");
                    return 1;
                }

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(obj, ExecutionJson.Options));
                }
                else
                {
                    foreach (var c in obj.Columns)
                    {
                        Console.WriteLine($"{c.Name}  {c.NativeType}  {(c.IsNullable ? "NULL" : "NOT NULL")}");
                    }
                }

                return 0;
            }

            case "scaffold":
            {
                var name = RequireObject(args);
                var targetObject = GetOption(args, "--target-object");
                if (string.IsNullOrWhiteSpace(targetObject))
                {
                    Console.Error.WriteLine("ERROR  catalog scaffold requires --target-object <schema.table>.");
                    return 1;
                }

                var obj = await service.IntrospectAsync(source, name, kind).ConfigureAwait(false);
                if (obj is null)
                {
                    Console.Error.WriteLine($"ERROR  object {name.QualifiedName} was not found.");
                    return 1;
                }

                var keys = (GetOption(args, "--keys") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // --detect-keys fills keyColumns from live unique-key detection when the operator did not pin --keys,
                // feeding the same detector the standalone verb uses. Notes go to stderr so stdout stays clean YAML.
                if (keys.Length == 0 && args.Contains("--detect-keys"))
                {
                    keys = await DetectTopKeyAsync(provider, source, kind, name, obj.Columns.Select(c => c.Name).ToList(), SampleOption(args)).ConfigureAwait(false);
                    Console.Error.WriteLine(keys.Length > 0
                        ? $"  detected key: {string.Join(", ", keys)}"
                        : "  no unique key detected; scaffolding without keyColumns.");
                }

                var yaml = CatalogScaffolder.ToIngestionYaml(obj, ScaffoldOptionsFor(args, source, kind, targetObject, GetOption(args, "--name"), keys));

                var outPath = GetOption(args, "--out", "-o");
                if (outPath is not null)
                {
                    await File.WriteAllTextAsync(outPath, yaml).ConfigureAwait(false);
                    Console.WriteLine($"Wrote ingestion-flow scaffold to {outPath}");
                }
                else
                {
                    Console.Write(yaml);
                }

                return 0;
            }

            case "scaffold-all":
            {
                var outDir = GetOption(args, "--out", "-o");
                if (string.IsNullOrWhiteSpace(outDir))
                {
                    Console.Error.WriteLine("ERROR  catalog scaffold-all requires --out <directory> for the generated flow files.");
                    return 1;
                }

                var targetSchema = GetOption(args, "--target-schema");
                var scope = new ObjectScope { Database = database, Schema = GetOption(args, "--schema") };
                var bulkQuery = query with { Limit = ParseIntOption(args, 1000, "--limit") };
                var page = await service.ObjectsAsync(source, scope, bulkQuery, kind).ConfigureAwait(false);
                if (page.Items.Count == 0)
                {
                    Console.Error.WriteLine("ERROR  no tables or views matched (check --schema/--like and the connection).");
                    return 1;
                }

                Directory.CreateDirectory(outDir);
                var written = 0;
                foreach (var item in page.Items)
                {
                    var obj = await service.IntrospectAsync(source, new ThreePartName { Database = database, Schema = item.Schema, Name = item.Name }, kind).ConfigureAwait(false);
                    if (obj is null)
                    {
                        Console.Error.WriteLine($"WARN  {item.Schema}.{item.Name} disappeared during scaffolding; skipped.");
                        continue;
                    }

                    var targetObject = $"{targetSchema ?? item.Schema}.{item.Name}";
                    var yaml = CatalogScaffolder.ToIngestionYaml(obj, ScaffoldOptionsFor(args, source, kind, targetObject, flowName: null, keys: []));
                    var file = Path.Combine(outDir, SafeFileName($"{item.Schema}.{item.Name}") + ".yaml");
                    await File.WriteAllTextAsync(file, yaml).ConfigureAwait(false);
                    Console.WriteLine($"  {file}");
                    written++;
                }

                Console.WriteLine($"Scaffolded {written} flow file(s) of {page.Total} matching object(s){(page.HasMore ? " (raise --limit for the rest)" : string.Empty)}.");
                return 0;
            }

            default:
                Console.Error.WriteLine(
                    "Usage: sqlflow catalog <databases|schemas|tables|search|columns|scaffold|scaffold-all> --source <ref> [--provider mysql|postgres|oracle] [options]");
                return 1;
        }
    }

    /// <summary>
    /// <c>sqlflow detect-unique-key --source &lt;ref&gt; --object [db.]schema.table [--sample N] [--max-columns K]</c>:
    /// finds the minimal column set(s) that uniquely identify the table's rows, with no prior knowledge of its keys.
    /// A key the database itself declares (an enabled, unfiltered unique index or constraint) is reported straight
    /// from metadata without reading a row (<c>--no-metadata</c> forces profiling instead); columns whose types can
    /// never form a practical key are excluded up front. Otherwise columns are ranked by how identifying they are; an
    /// already-unique single column wins outright, else a composite is grown greedily and reduced to a minimal key. A
    /// <c>--sample</c> run profiles a random sample for speed and then verifies each surviving candidate against the
    /// whole table, so a reported key is never merely sample-based. The profiling is T-SQL, so the source must be
    /// SQL Server / Azure SQL. Exit 0 when a unique key is found, 2 when not.
    /// </summary>
    private static async Task<int> RunDetectUniqueKeyAsync(IServiceProvider provider, string[] args)
    {
        var source = GetOption(args, "--source") ?? "${env:SQLFLOW_SOURCE}";
        if (SecretHygiene.LooksLikeEmbeddedSecret(source))
        {
            Console.Error.WriteLine(
                "WARN  --source embeds a credential on the command line (it lands in shell history). Prefer the " +
                "canonical ${env:SQLFLOW_SOURCE}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, " +
                "with local values in the git-ignored .sqlflow/env file.");
        }

        var kind = ParseProviderOption(GetOption(args, "--provider"));
        if (kind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
        {
            Console.Error.WriteLine("ERROR  detect-unique-key profiles with T-SQL; --provider must be mssql or azdb.");
            return 1;
        }

        var name = RequireObject(args);
        var json = args.Contains("--json");

        // The columns come from the same catalog introspection every other command uses; the live measurement then
        // runs on the resolved connection, so both see one source of truth.
        var obj = await provider.GetRequiredService<CatalogService>().IntrospectAsync(source, name, kind).ConfigureAwait(false);
        if (obj is null)
        {
            Console.Error.WriteLine($"ERROR  object {name.QualifiedName} was not found.");
            return 1;
        }

        var columns = obj.Columns.Select(c => c.Name).ToList();
        if (columns.Count == 0)
        {
            Console.Error.WriteLine($"ERROR  object {name.QualifiedName} has no columns to profile.");
            return 1;
        }

        var options = new UniqueKeyOptions
        {
            MaxKeyColumns = Math.Max(1, ParseIntOption(args, 4, "--max-columns")),
            MaxCandidates = Math.Max(1, ParseIntOption(args, 5, "--max-candidates")),
            Verify = !args.Contains("--no-verify"),
        };

        var resolved = await provider.GetRequiredService<IConnectionResolver>()
            .ResolveAsync(source, ConnectionRole.Source, kind).ConfigureAwait(false);

        await using var probe = await SqlServerUniquenessProbe
            .CreateAsync(resolved.CanonicalString, name.QualifiedName, columns, SampleOption(args),
                trustDeclaredKeys: !args.Contains("--no-metadata"))
            .ConfigureAwait(false);
        var report = (await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, options).ConfigureAwait(false))
            with { ObjectName = name.QualifiedName, ExcludedColumns = probe.ExcludedColumns };

        if (GetOption(args, "--out", "-o") is { } outPath)
        {
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, ExecutionJson.Options)).ConfigureAwait(false);
            if (!json)
            {
                Console.WriteLine($"Wrote unique-key report to {outPath}");
            }
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, ExecutionJson.Options));
        }
        else
        {
            PrintUniqueKeyReport(report);
        }

        return report.Candidates.Any(c => c.IsUnique) ? 0 : 2;
    }

    private static void PrintUniqueKeyReport(UniqueKeyReport report)
    {
        var scope = report.Sampled
            ? $"{report.TotalRows} row(s), profiled on a sample of {report.ScannedRows}"
            : $"{report.TotalRows} row(s)";
        Console.WriteLine($"{report.ObjectName}: {scope}");

        if (report.Candidates.Count == 0)
        {
            Console.WriteLine("  no candidate keys.");
        }
        else
        {
            Console.WriteLine("  candidates (most trustworthy first):");
            var rank = 1;
            foreach (var candidate in report.Candidates)
            {
                var approx = candidate.Estimated ? "~" : string.Empty;
                var status = candidate.IsUnique
                    ? candidate.Declared ? "UNIQUE (declared)" : candidate.Verified ? "UNIQUE" : "unique on the sample (unverified)"
                    : $"not unique ({approx}{candidate.Duplicates} duplicate row(s)"
                      + (candidate.Nulls > 0 ? $", {approx}{candidate.Nulls} null row(s)" : string.Empty)
                      + (candidate.Estimated ? ", sample estimate" : string.Empty) + ")";
                Console.WriteLine(
                    $"   {rank++}. [{string.Join(", ", candidate.Columns)}]  {status}  selectivity {approx}{candidate.Selectivity.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
        }

        if (report.ExcludedColumns.Count > 0)
        {
            Console.WriteLine(
                $"  excluded from the search: {string.Join(", ", report.ExcludedColumns.Select(e => $"{e.Column} ({e.Reason})"))}");
        }

        if (report.Note is not null)
        {
            Console.WriteLine($"  note: {report.Note}");
        }
    }

    /// <summary>Runs unique-key detection for scaffolding and returns the top confirmed key's columns (empty when
    /// none is found, or the source is not SQL Server). Shares the detector and probe the standalone verb uses.</summary>
    private static async Task<string[]> DetectTopKeyAsync(
        IServiceProvider provider, string source, DataSourceKind? kind, ThreePartName name, IReadOnlyList<string> columns, int? sample)
    {
        if (kind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle || columns.Count == 0)
        {
            return [];
        }

        var resolved = await provider.GetRequiredService<IConnectionResolver>()
            .ResolveAsync(source, ConnectionRole.Source, kind).ConfigureAwait(false);
        await using var probe = await SqlServerUniquenessProbe
            .CreateAsync(resolved.CanonicalString, name.QualifiedName, columns, sample).ConfigureAwait(false);
        var report = await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, new UniqueKeyOptions()).ConfigureAwait(false);

        return report.Candidates.FirstOrDefault(c => c.IsUnique && c.Verified)?.Columns.ToArray() ?? [];
    }

    /// <summary>The <c>--sample</c> option as a nullable size: absent means auto-sample (large tables only), a value
    /// (0 for a full scan, or a positive count) is used verbatim.</summary>
    private static int? SampleOption(string[] args)
    {
        var raw = GetOption(args, "--sample");
        if (raw is null)
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    }

    /// <summary>Builds the scaffold options with the no-secret embedding rule: a whole <c>${...}</c> reference
    /// is embedded verbatim; anything else (a literal connection string, an @alias) becomes an env-var
    /// placeholder the operator fills in, so a secret can never leak into a generated file.</summary>
    private static ScaffoldOptions ScaffoldOptionsFor(string[] args, string source, DataSourceKind? kind, string targetObject, string? flowName, IReadOnlyList<string> keys)
    {
        var target = GetOption(args, "--target");
        return new ScaffoldOptions
        {
            SourceConnection = EmbeddableReference(source, "${env:SQLFLOW_SOURCE}"),
            SourceProvider = kind switch
            {
                DataSourceKind.MySQL => "mysql",
                DataSourceKind.PostgreSQL => "postgres",
                DataSourceKind.Oracle => "oracle",
                DataSourceKind.AZDB => "azdb",
                _ => null,
            },
            TargetConnection = EmbeddableReference(target, "${env:SQLFLOW_DW}"),
            TargetObject = targetObject,
            FlowName = flowName,
            KeyColumns = keys,
        };
    }

    private static string EmbeddableReference(string? reference, string placeholder)
        => reference is not null && reference.StartsWith("${", StringComparison.Ordinal) && reference.EndsWith('}')
            ? reference
            : placeholder;

    private static DataSourceKind? ParseProviderOption(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        null or "" or "mssql" or "sqlserver" => null,
        "azdb" => DataSourceKind.AZDB,
        "mysql" => DataSourceKind.MySQL,
        "postgres" or "postgresql" => DataSourceKind.PostgreSQL,
        "oracle" => DataSourceKind.Oracle,
        _ => throw new SqlFlowException($"Unknown --provider '{provider}'. Allowed: mssql, azdb, mysql, postgres, oracle."),
    };

    /// <summary>The scaffolded file's name, sanitized by the one shared naming rule so a quoted identifier
    /// containing a character Windows forbids cannot produce a file that only exists on Linux.</summary>
    private static string SafeFileName(string name) => RunHistoryWriter.SafeName(name);

    private static void Output<T>(bool json, IReadOnlyList<T> items, Func<T, string> line)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(items, ExecutionJson.Options));
            return;
        }

        foreach (var item in items)
        {
            Console.WriteLine(line(item));
        }
    }

    private static ThreePartName RequireObject(string[] args)
    {
        var raw = GetOption(args, "--object");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SqlFlowException("This command requires --object <schema.object | database.schema.object>.");
        }

        var parts = new List<string>();
        var token = new StringBuilder();
        var inBracket = false;
        foreach (var c in raw)
        {
            if (!inBracket && c == '.')
            {
                parts.Add(Unbracket(token.ToString()));
                token.Clear();
            }
            else
            {
                if (c == '[' && !inBracket)
                {
                    inBracket = true;
                }
                else if (c == ']' && inBracket)
                {
                    inBracket = false;
                }

                token.Append(c);
            }
        }

        parts.Add(Unbracket(token.ToString()));
        parts = parts.Where(p => p.Length > 0).ToList();

        return parts.Count switch
        {
            >= 3 => new ThreePartName { Database = parts[^3], Schema = parts[^2], Name = parts[^1] },
            2 => new ThreePartName { Schema = parts[0], Name = parts[1] },
            _ => throw new SqlFlowException($"--object must be 'schema.object' or 'database.schema.object'; got '{raw}'."),
        };

        static string Unbracket(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : trimmed;
        }
    }

    private static ServiceProvider BuildServiceProvider(bool verbose, bool json)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = true;
            });
            // Diagnostics belong on stderr for a CLI: stdout carries the verb's own output (tables, YAML,
            // and especially --json payloads a script pipes into a parser).
            builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });

        // The engine is wired once in SqlFlow.Execution and shared by every host (CLI, control plane, workers),
        // so there is a single engine composition to maintain. The CLI then overrides the DocumentExecutor
        // registration so its hygiene/history warnings reach standard error (the extension registers it with no
        // sink); a later registration of the same service wins. With --json the engine's live event stream is
        // re-aimed at stderr too, so stdout is exactly one parsable JSON document.
        services.AddSqlFlowEngine();
        services.AddSingleton(sp => new DocumentExecutor(sp, Console.Error.WriteLine));
        if (json)
        {
            services.AddSingleton<IFlowEventSink>(new ConsoleFlowEventSink(Console.Error));
        }

        return services.BuildServiceProvider();
    }

    private static void PrintPlan(FlowPlan plan)
    {
        Console.WriteLine($"Plan for '{plan.Flow.Name}' -> {plan.Flow.Target.QualifiedName}");
        Console.WriteLine(plan.Actual is null ? "  target : does not exist (will create)" : "  target : exists");
        Console.WriteLine($"  columns to add : {plan.Delta.ColumnsToAdd.Count}");
        Console.WriteLine($"  columns to widen : {plan.Delta.ColumnsToAlter.Count}");
        Console.WriteLine($"  load mode      : {plan.Flow.Load.Mode}");

        if (plan.DdlStatements.Count == 0)
        {
            Console.WriteLine("  generated DDL  : (none)");
        }
        else
        {
            Console.WriteLine("  generated DDL  :");
            foreach (var statement in plan.DdlStatements)
            {
                foreach (var line in statement.Split('\n'))
                {
                    Console.WriteLine($"    {line.TrimEnd('\r')}");
                }
            }
        }

        PrintTrace(plan.Trace, totalMs: null);
    }

    private static void PrintResult(FlowResult result)
    {
        var status = result.Status == FlowStatus.Success ? "OK" : "FAILED";
        Console.WriteLine($"{status}  '{result.FlowName}': {result.RowsLoaded} row(s) loaded; {result.DdlExecuted.Count} DDL statement(s).");
        if (result.Error is not null)
        {
            Console.WriteLine($"  error: {result.Error}");
        }

        PrintTrace(result.Trace, result.TotalMs);
    }

    private static void PrintTrace(IReadOnlyList<TraceEntry> trace, double? totalMs)
    {
        if (trace.Count == 0)
        {
            return;
        }

        Console.WriteLine("  trace:");
        foreach (var entry in trace)
        {
            var marker = entry.Succeeded ? "ok  " : "FAIL";
            var rows = entry.Rows is { } r ? $"  ({r} rows)" : string.Empty;
            var detail = entry.Detail is not null ? $"  {entry.Detail}" : string.Empty;
            Console.WriteLine($"    {marker}  {entry.Operation,-22} {entry.ElapsedMs,8:F1} ms{rows}{detail}");
        }

        if (totalMs is { } total)
        {
            Console.WriteLine($"          {"TOTAL",-22} {total,8:F1} ms");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            sqlflow - metadata-driven ETL for SQL Server

            Usage:
              sqlflow validate <pipeline.yaml>   Validate a pipeline definition
              sqlflow plan     <pipeline.yaml>   Show the SQL that would run (changes nothing; file flows)
              sqlflow run      <pipeline.yaml>   Execute the pipeline (Ctrl+C aborts the in-flight statement and rolls back)
                               [--full]          Backfill: ignore the watermark, read everything the flow selects
                               [--from <date>]   Backfill: externally-bounded window low bound (file date /
                                                 incremental date column / export or init-load chunk plan)
                               [--to <date>]     Backfill: the window's high bound (requires --from)
                               [--file-pattern <glob>]  Backfill: narrow a file flow to one glob this run
                               [--assertions-only]      Evaluate the flow's data-quality assertions (manual-mode
                                                 ones included) against the current target; loads nothing (ing flows)
                               [--health-check]  Run the flow's embedded healthCheck: block (the derived hc
                                                 pipeline) instead of the load; nothing is loaded (ing flows)
              sqlflow infer    <pipeline.yaml>   Profile the loaded table and output inferred types (JSON)
              sqlflow discover <pipeline.yaml>   Scan a JSON/XML source, auto-detect the record grain, and report its path structure
              sqlflow paths    <file|folder>     List every path in a JSON/NDJSON/XML file or folder
              sqlflow flatten  <file|folder>     Emit the flatten formula (a runnable flow stub); --data dumps CSV
              sqlflow healthcheck --object [db.]schema.table [--source <ref>]
                                                 ML anomaly check against ANY table, no pipeline file: auto-detects
                                                 the date column, learns the expected per-date metric (trend +
                                                 calendar via AutoML), and reports missing data, anomalies (robust
                                                 generalized-ESD), level shifts (PELT), and date-quality issues
                                                 (--source defaults to the canonical ${env:SQLFLOW_SOURCE})
              sqlflow detect-unique-key --object [db.]schema.table [--source <ref>] [--sample N] [--max-columns K]
                                                 Find the minimal column set(s) that uniquely identify a table's
                                                 rows, with no prior key knowledge. A key the database already
                                                 declares (unique index/constraint) is answered from metadata
                                                 without reading a row (--no-metadata forces profiling); unkeyable
                                                 column types (LOB, float, CLR, ...) are excluded up front. Else
                                                 ranks columns, takes an already-unique single column or grows a
                                                 minimal composite, searching a random sample in batched passes
                                                 (large tables auto-sample; --sample N sets the size, --sample 0
                                                 forces a full scan), then each finalist is confirmed against the
                                                 whole table (--no-verify skips that and returns fast, sample-only
                                                 candidates). T-SQL source. Exit 0 when a unique key is found, 2
                                                 when none is.
              sqlflow lineage  <folder>          AST-based lineage over a flow estate: declared (YAML) plus
                                                 observed (run artifacts) offline; --connect adds the derived tier
                                                 (catalog + sys.sql_modules parsed with ScriptDom: views and proc
                                                 bodies expand). Computes the execution plan: waves of flows that
                                                 can run concurrently, cycles traced into a fallback wave. Writes
                                                 the canonical .sqlflow/lineage/lineage.json. --of <object|flow>
                                                 walks impact (--down, default) or dependencies (--up);
                                                 --explain <flow> traces why a flow is in its wave (its
                                                 dependencies, reads/writes, and tier provenance); --dump-facts
                                                 writes the raw pre-merge facts to .sqlflow/lineage/facts.json
                                                 (what each tier contributed); --strict exits 2 on cycles
              sqlflow auth     [--scope storage|keyvault|arm|<uri>]
                                                 Verify Azure auth in THIS environment: reports the resolved mode
                                                 (SQLFLOW_AZURE_AUTH: sp / mi / cli / default chain) and actually
                                                 acquires a token for the scope (default storage), through the one
                                                 credential factory Key Vault, invoke, and DuckDB cloud reads all
                                                 use. Exit 0 on a token, 1 on failure.
              sqlflow db <migrate|sync|status> [path] [--db <conn-ref>] [--create] [--repo name] [--repo-url url] [--connect]
                                                 Database mode: the EF-managed shadow catalog (a read-model of the
                                                 git/YAML estate + on-disk run history + lineage, for the GUI).
                                                 Each YAML flow is mapped into a row (kind, source/target, the full
                                                 definition as queryable JSON) and lineage objects/edges, so you can
                                                 query across files and repos. 'migrate' upgrades an EXISTING catalog
                                                 to the current schema version; add --create to provision a new one
                                                 (create the database, or initialise the catalog in an empty
                                                 database) - without --create a missing or non-catalog database is
                                                 refused, so a mistyped --db never provisions the wrong (possibly
                                                 production) server; 'sync' projects the estate, run.json detail, and
                                                 lineage under [path] into it (upgrading an existing catalog first),
                                                 attributed to --repo (default: the folder name); --connect adds the
                                                 derived lineage tier (fetches object metadata from the live
                                                 database, linking flows across repos through shared objects);
                                                 'status' lists applied vs pending migrations.
                                                 --db defaults to ${env:SQLFLOW_CATALOG_DB}.
              sqlflow worker   --url <control-plane> [--token <ref>] [--pool a,b]
                               [--poll-seconds N] [--drain-seconds N]
                                                 Run as a self-hosted compute node: poll the control plane's
                                                 dispatcher for work over HTTP (the node protocol, authenticated
                                                 with a personal access token carrying the 'node' scope: --token or
                                                 SQLFLOW_TOKEN, a ${env:...}/${keyvault:...} reference resolved
                                                 here), execute each handed-out run through the same engine on THIS
                                                 host (resolving every credential from this node's own environment)
                                                 and report each outcome. The node needs no catalog connection: the
                                                 run's definition, its snapshotted YAML, its lineage context and its
                                                 live trace all travel over the same protocol. Placement is the
                                                 dispatcher's, so any number of nodes is safe at once. --url defaults
                                                 to SQLFLOW_URL. Runs until Ctrl+C or SIGTERM, which
                                                 stops taking work and then lets the in-flight runs finish and report
                                                 their outcomes (--drain-seconds, default 540; keep it under the
                                                 orchestrator's termination grace period). --pool sets the pools this
                                                 node serves (it always takes untargeted runs; with --pool it also
                                                 takes runs routed to those pools). --poll-seconds (default 30) is how
                                                 long each poll waits for work before returning empty.
              sqlflow runs cancel <runId> [--db <conn-ref>]
                                                 Cancel a run. With a control plane configured (--url/SQLFLOW_URL)
                                                 this goes through the API; with --db, or with no control plane
                                                 configured, it talks to the shadow catalog directly (the break-glass
                                                 route that works when the control plane is down). A still-queued run
                                                 is dequeued outright; a run already executing has a cancel request
                                                 stamped that its node observes to abort the in-flight statement and
                                                 record it cancelled. --db defaults to ${env:SQLFLOW_CATALOG_DB}.
              sqlflow user reset-password <username> [--db <conn-ref>]
                                                 Reset a local user's password straight against the catalog (no
                                                 control plane, no token; needs only database access), so an admin
                                                 who is locked out can recover without the break-glass bootstrap
                                                 secret. The new password is read from a hidden prompt (confirmed,
                                                 never a flag, so it stays out of shell history), length-checked, and
                                                 hashed exactly as the control plane verifies it. SSO (Entra) users
                                                 are refused (their credential lives in the provider). --db defaults
                                                 to ${env:SQLFLOW_CATALOG_DB}.

            Control plane (remote): the same API the GUI uses, so anything verified in the browser can be
            verified from a terminal or a test script. The target resolves from --url or SQLFLOW_URL; the
            credential from --token, then SQLFLOW_TOKEN (both suppliable via the git-ignored .sqlflow/env),
            then the per-URL store 'login' writes (~/.sqlflow/credentials.json, relocatable with
            SQLFLOW_CREDENTIALS_FILE). Human output on stdout, notes on stderr; --json switches
            stdout to the raw API shapes. Exit codes: 0 ok, 1 error or a followed run that did not succeed,
            130 on Ctrl+C.
              sqlflow health                     Probe /health/live and /health/ready (anonymous). Exit 0 when both pass.
              sqlflow login    [--username <u>] [--device] [--with-token]
                               [--token-name <n>] [--expires-days <N>|--no-expiry] [--scopes "read operate"] [--no-store]
                                                 Sign in and store a personal access token for the URL. Default path:
                                                 username + password (hidden prompt or piped stdin; never a flag); the
                                                 short-lived session then mints a PAT, and the PAT is what is stored
                                                 (revocable server-side; the password is never written). --device runs
                                                 the browser device grant (for SSO/Entra accounts); --with-token stores
                                                 a pasted PAT. --no-store prints the minted secret once instead (CI).
              sqlflow logout                     Revoke the stored token server-side and remove it locally.
              sqlflow trigger  --repo <name|id> --flow <f> [--scope flow|node]
                               [--pool <p>] [--commit <sha>] [--full] [--from <date>] [--to <date>]
                               [--file-pattern <glob>] [--source-filter <predicate>] [--assertions-only]
                               [--include-all] [--preview] [--follow]
                                                 Enqueue a run on the fleet (POST /runs), exactly as the GUI's trigger
                                                 dialog does: scope flow (default) or node (the flow + its lineage
                                                 descendants; only mode: auto descendants by default, --include-all
                                                 widens to manual and disabled ones). A whole source runs through its
                                                 schedule ('schedules run'), whose membership is what a fire runs.
                                                 --preview shows the members and waves without enqueuing; backfill
                                                 flags are the same as a local run; --follow attaches to the live
                                                 trace (or the group's member stream) and exits by the terminal
                                                 outcome.
              sqlflow runs list [--status s] [--flow name] [--batch b] [--kind k] [--repo r] [--group g] [--latest]
              sqlflow runs show <runId> [--files --statements --assertions --keys --metrics]
              sqlflow runs trace <runId> [--follow]
                                                 The run inbox, one run's full header (with drill-down sections), and
                                                 the consolidated trace: plain text at rest, live streamed with
                                                 --follow (reconnects with resume cursors on a drop).
              sqlflow groups show <groupId> [--follow] | cancel <groupId> | rerun <groupId> [--follow]
                                                 A node/batch run group: rollup + members (live with --follow), group
                                                 cancellation, and rerun (a fresh re-expansion of the same anchor).
              sqlflow whoami                     Who the resolved credential authenticates as (subject, role, scopes)
                                                 and where the credential came from (--token / SQLFLOW_TOKEN / store).
              sqlflow summary                    The dashboard rollup: estate size, run queue, fleet, schedules, sync.
              sqlflow nodes                      The worker fleet with heartbeat-derived online/offline state.
              sqlflow schedules list | show <id> | create --repo r --flow f[,f2,...] (--cron <expr>|--interval <seconds>)
                               [--name <n>] [--timezone tz] [--max-concurrency <n>] [--disabled] [--catchup]
                               | run <id> | pause <id> | resume <id> | delete <id>
                                                 The scheduling surface: a schedule owns a member SET (what a fire
                                                 runs, wave-ordered); 'run' fires it now without moving its cadence.
              sqlflow repos    list | show <name|id> | sync <name|id>
                               | register --name r --remote-url u [--branch b] [--interval s]
                                 [--credential-ref ${env:GIT_TOKEN}] [--credential-user u] [--disabled]
                               | discover --remote-url u [--branch b] [--credential-ref ...]
                                                 Synced repos and their managed git sources: register a source (the
                                                 credential is always a ${...} reference), preview a remote's flows
                                                 without importing, and force a sync (git source or local re-sync).
              sqlflow pipelines list [--repo r --kind k --active true|false --name x]
                               | show <id> [--yaml|--definition] | columns <id> [--kind declared|detected]
                               | files <id> [--search x]         The pipeline registry; --yaml/--definition print the
                                                 raw document on stdout (pipe- and LLM-friendly).
              sqlflow datasources list | test|databases|schemas|objects|search|introspect|detect-unique-key
                                 --ref <${env:NAME}|@alias> [--kind k] [--pool p] [--database d] [--schema s]
                                 [--object [schema.]name] [--like x] [--term x] [--limit n] [--no-wait]
                               | tasks [--status s] | task <id> | cancel <id>
                                                 The remote twins of 'catalog'/'detect-unique-key': queued compute
                                                 tasks executed by a worker node INSIDE the network (no direct DB
                                                 reachability needed here); the worker's JSON result prints on stdout.
              sqlflow search <term> [--objects|--columns|--definitions|--files|--flows|--flow-columns|--statements]
                                                 Global catalog search; default shows every category's count + top hits.
                                                 A multi-word term matches every word (anywhere in a row), so
                                                 "ferry passengers" finds FerryPassengers_PerDeparture.
                                                 --flow-columns searches the columns FLOWS produce (name, source
                                                 column, or computing expression); --statements searches the SQL
                                                 runs actually executed, last 90 days.
              sqlflow lineage objects|edges|waves|script ...
                                                 The synced lineage graph as DATA (a console cannot draw the GUI's
                                                 graph, but the dataset prints and --json feeds an LLM or a test):
                                                 objects [--name --server --database --schema --kind]; edges --repo r
                                                 [--object key --relation read|write --tier t]; waves --repo r (the
                                                 execution plan); script <key|flow> (the code behind any node).
              sqlflow doctor                     One pass over this machine's SQLFlow setup: the resolved .sqlflow/env,
                                                 which SQLFLOW_* variables are set (values never printed), control
                                                 plane live/ready + credential identity, and catalog DB reachability
                                                 with schema currency. Unconfigured surfaces are SKIP, not failures.
              sqlflow runs local [folder] [--flow x] [--last N]
                                                 The on-disk run history (.sqlflow/runs artifacts) next to the
                                                 pipeline files, newest first; no catalog or control plane needed.
              sqlflow completions bash|zsh|powershell
                                                 Print a shell completion script (e.g. source <(sqlflow completions bash)).

            'validate' also accepts a FOLDER (and --json on either shape): every *.yaml/*.yml under it is
            validated through the same loader, one line per document, exit 0 only when all parse; the CI gate.

            Six pipeline kinds share validate/run, discriminated by the document's flowType key:
              (none)         a file flow: CSV/JSON/XML/XLS/Parquet into SQL Server (the default)
              flowType: ing  table-to-table ingestion: staged copy with dynamic schema evolution, keyed
                             upsert, incremental loading (scaffold one with 'catalog scaffold')
              flowType: exp  file export: a SQL Server table/view to CSV or Parquet files, optionally
                             chunked by day/month/key windows
              flowType: sp   stored procedure: EXEC one existing procedure on a resolved server
              flowType: inv  invoke: trigger an Azure Data Factory pipeline or Automation runbook and
                             wait for it (ing/exp/sp flows reference the same 'invokes:' as hooks)
              flowType: hc   ML health check: one or more metrics (COUNT(*), SUM(...), ...) learned per
                             date (Theil-Sen trend + AutoML calendar model) and judged by robust
                             generalized-ESD, with PELT level shifts, missing-data and trailing-gap
                             detection, and data-quality probes (models kept in .sqlflow/state)
              flowType: scm  source control: scripts a managed SQL Server database's objects with SMO into a
                             git working tree (one folder per object type, the legacy layout) and commits the
                             snapshot, pushing to a BitBucket or GitHub remote over HTTPS. Re-running over time
                             is what records the schema's change history. The git credential is a ${env:...}
                             reference (a BitBucket app password or a GitHub token), never in the document.
                             --dry-run scripts and writes the tree without committing; --no-push commits locally
              flowType: batch  ordered multi-flow run: lineage computes concurrency waves over the member flows
                             (members.include/exclude globs), each wave runs concurrently (maxParallel), and the
                             whole wave finishes before the next starts. onError stop|continue controls failure
                             handling; ignoreErrors lists members allowed to fail; members.inactive deactivates
                             a member for the run. connect auto|always|never picks the lineage tier for ordering
            Every run writes its artifacts to a .sqlflow/runs/<flow>/ folder next to the pipeline file:
            run.json (the result), run.log (the canonical step-by-step log), trace.sql (the generated SQL);
            an hc run adds healthcheck.json (the full scored series). --show-sql prints the generated SQL
            to the console after any run; --log-level info|debug|trace sets the run.log detail for
            ing/exp/sp/hc runs (trace weaves every statement into the timeline).

            Secrets never live in pipeline files. Documents carry references (${env:NAME},
            ${keyvault:vault/secret}) or just a bare connection name, which resolves the canonical
            ${env:SQLFLOW_CONN_<NAME>}. Local development values go in the git-ignored .sqlflow/env file
            (KEY=VALUE, found next to the document or in any parent folder); the process environment always
            wins, on every OS .NET runs on. See docs/environment-variables.md.

            paths/flatten/discover work for JSON and XML; the format is taken from the file extension
            (or --pattern for a folder). Flatten rule flags map to each format's option keys.

            Source discovery and replication scaffolding (SQL Server, MySQL, PostgreSQL, Oracle):
              sqlflow catalog tables       --source ${env:SRC} [--provider mysql|postgres|oracle] [--schema s] [--like x]
              sqlflow catalog databases|schemas|search|columns ... same flags
              sqlflow catalog scaffold     --source ... --object schema.table --target ${env:DW} --target-object raw.table
              sqlflow catalog scaffold-all --source ... --target ${env:DW} --out ./flows [--schema s] [--target-schema raw]
            scaffold/scaffold-all generate RUNNABLE flow files: keys come from the primary key, incremental
            candidates are suggested, and secrets are never embedded (non-${...} references become placeholders).

            Options:
              -v, --verbose                      Emit per-stage debug timing
              -o, --out <file>                   Write output to a file (infer/flatten)
                  --json                         Output the result as JSON (run/catalog)
                  --show-sql                     Print the generated SQL after a run
                  --log-level <info|debug|trace> run.log detail for ing/exp/sp/hc runs (default info)
                  --dry-run                      scm: script and write the working tree without committing
                  --no-push                      scm: commit locally but do not push to the remote
                  --retrain                      Train fresh health-check models this run (hc/healthcheck)
                  --fail-on-anomaly              Exit 2 when a health check finds anomalies (CI gating)
                  --date-column <name>           healthcheck: the date column (omit to auto-detect)
                  --base-value <expr>            healthcheck: the aggregate to monitor (default COUNT(*))
                  --filter <bool expr>           healthcheck: ANDed into the series query
                  --threshold <sigma>            healthcheck: severity floor in robust sigmas (default 2.0)
                  --alpha <p>                    healthcheck: ESD significance level (default 0.025)
                  --budget <seconds>             healthcheck: AutoML budget (default 30; 120 with --state-dir)
                  --maturity <days>              healthcheck: trailing days still arriving (default 1)
                  --state-dir <dir>              healthcheck: persist models + run history here (else in-memory)
                  --values                       List only value paths, one per line (paths)
                  --data                         Output the flattened rows as CSV instead of the formula (flatten)
                  --provenance                   Keep the _DW provenance columns in --data output (flatten)
                  --root <path>                  Records under this path (rootPath for JSON, rowXPath for XML)
                  --include / --exclude / --explode <paths>   Flatten rules, comma-separated (flatten)
                  --keep <paths>                 Keep subtrees as a string column (jsonPaths / xmlPaths)
                  --aliases <col=/a|/b; ...>     Schema-evolution aliases: many paths to one column (flatten)
                  --array <to_json|first_element|join|count|skip|explode>   JSON array handling (flatten)
                  --repeat <to_xml|first_element|last_element|join|count|skip|explode>   XML repeat handling
                  --separator <s>                Column-name separator (flatten; default _)
                  --map <path=col;...>           Column-name overrides (flatten)
                  --pattern <glob>               File glob when the target is a folder (default *.json / *.xml)
                  -r, --recursive                Recurse into sub-folders (paths/flatten)
                  --max-files <n>                Files to scan (default 100)
                  --max-records <n>              Records to scan, 0 = all (default 0)
                  --max-depth <n>                Max nesting depth to inspect (discover 10, paths/flatten 20)
                  --default-type <sqltype>       schema.defaultColumnType in the emitted flow (flatten; default varchar(255))
              -h, --help                         Show this help
            """);
    }

    private static IFlattenIntrospector? IntrospectorFor(IServiceProvider provider, string type)
        => FlattenSourceSpec.IntrospectorFor(provider.GetServices<ISourceReader>(), type);

    /// <summary>
    /// Synthesizes a JSON or XML source from a bare file or folder path for the path-only commands. The type
    /// is taken from the file extension (or the --pattern for a folder). With <paramref name="withFlatten"/>
    /// it threads the flatten options (mapped to the format's option keys); with
    /// <paramref name="suppressProvenance"/> the _DW columns are turned off for a clean data preview.
    /// </summary>
    private static SourceSpec SourceFromPath(string path, string[] args, bool withFlatten = false, bool suppressProvenance = false)
    {
        // The base spec (format inference, folder detection, record-grain override) is resolved by the shared
        // builder so the CLI and the control-plane endpoint interpret a location the same way; the flatten-rule
        // flags below are CLI-only input adaptation layered onto the same mutable options.
        var recursive = args.Any(a => a is "--recursive" or "-r");
        var (type, options) = FlattenSourceSpec.BuildBase(path, null, GetOption(args, "--pattern"), recursive, GetOption(args, "--root"));
        var isXml = type == "xml";

        if (withFlatten)
        {
            MapOption(options, args, "--include", "includePaths");
            MapOption(options, args, "--exclude", "excludePaths");
            MapOption(options, args, "--explode", "explodePaths");
            MapOption(options, args, "--aliases", "pathAliases");
            MapOption(options, args, "--separator", "separator");
            MapOption(options, args, "--join-separator", "joinSeparator");
            MapOption(options, args, "--map", "columnMappings");

            // Keep-as-string and array/repeat handling are named per format.
            if (GetOption(args, "--keep") is { } keep)
            {
                options[isXml ? "xmlPaths" : "jsonPaths"] = keep;
            }

            MapOption(options, args, "--json", "jsonPaths");
            MapOption(options, args, "--xml", "xmlPaths");
            MapOption(options, args, "--array", "arrayHandling");
            MapOption(options, args, "--repeat", "repeatHandling");
        }

        if (suppressProvenance)
        {
            foreach (var key in FlattenSourceSpec.ProvenanceOptionKeys)
            {
                options[key] = "false";
            }
        }

        return new SourceSpec { Type = type, Location = path, Options = options };
    }

    internal static void MapOption(Dictionary<string, string?> options, string[] args, string flag, string key)
    {
        if (GetOption(args, flag) is { } value)
        {
            options[key] = value;
        }
    }

    private static async Task WriteFormulaAsync(SourceSpec source, FlattenIntrospection introspection, string[] args)
    {
        var text = FlattenFlowYaml.Build(source, introspection, GetOption(args, "--default-type"));
        var outPath = GetOption(args, "--out", "-o");
        if (outPath is not null)
        {
            await File.WriteAllTextAsync(outPath, text).ConfigureAwait(false);
            Console.WriteLine($"Wrote flatten formula ({introspection.Formula.Columns.Count} column(s)) to {outPath}");
        }
        else
        {
            Console.Write(text);
        }
    }

    private static async Task FlattenToCsvAsync(ISourceReader reader, SourceSpec source, string[] args)
    {
        var columns = await reader.GetColumnsAsync(source).ConfigureAwait(false);
        var read = await reader.OpenAsync(source, columns).ConfigureAwait(false);
        await using var data = read.Reader;

        var maxRecords = ParseIntOption(args, 0, "--max-records");
        var outPath = GetOption(args, "--out", "-o");
        var toFile = outPath is not null;
        TextWriter writer = toFile ? new StreamWriter(outPath!, append: false) : Console.Out;

        try
        {
            await writer.WriteLineAsync(string.Join(",", columns.Select(c => CsvEscape(c.Name)))).ConfigureAwait(false);

            long rows = 0;
            while (await data.ReadAsync().ConfigureAwait(false))
            {
                if (maxRecords > 0 && rows >= maxRecords)
                {
                    break;
                }

                var cells = new string[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    cells[i] = data.IsDBNull(i)
                        ? string.Empty
                        : CsvEscape(Convert.ToString(data.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty);
                }

                await writer.WriteLineAsync(string.Join(",", cells)).ConfigureAwait(false);
                rows++;
            }

            if (toFile)
            {
                Console.WriteLine($"Wrote {rows} row(s), {columns.Count} column(s) to {outPath}");
            }
        }
        finally
        {
            if (toFile)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string CsvEscape(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static void PrintInventory(FlattenIntrospection introspection, bool valuesOnly)
    {
        var inventory = introspection.Inventory;
        if (valuesOnly)
        {
            foreach (var info in inventory.Paths.Where(p => p.Kind == SchemaPathKind.Value))
            {
                Console.WriteLine(info.Path);
            }

            return;
        }

        Console.WriteLine(
            $"{inventory.Paths.Count} path(s) across {inventory.RecordsScanned} record(s) in {inventory.FilesScanned} file(s).");

        if (inventory.Paths.Count == 0)
        {
            Console.WriteLine("  (no records found - check the path, file pattern, and --root)");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"PATH",-50} {"KIND",-10} {"COLUMN",-28} PRESENCE");
        foreach (var info in inventory.Paths)
        {
            var kind = info.Kind switch
            {
                SchemaPathKind.Value => "value",
                SchemaPathKind.Repeating => "repeating",
                _ => "container",
            };
            var column = info.Kind == SchemaPathKind.Container ? "-" : info.Column;
            var presence = info.RecordCount >= inventory.RecordsScanned
                ? "all"
                : $"{info.RecordCount}/{inventory.RecordsScanned}";
            Console.WriteLine($"  {Truncate(info.Path, 50),-50} {kind,-10} {Truncate(column, 28),-28} {presence}");
        }

        // Two different paths can fold onto the same column name; under the default flatten the later one
        // silently overwrites the earlier. Surfacing it lets the author add a columnMapping to disambiguate.
        var collisions = inventory.Paths
            .Where(p => p.Kind != SchemaPathKind.Container && p.Column.Length > 0 && !p.Path.Contains("[*]", StringComparison.Ordinal))
            .GroupBy(p => p.Column, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (collisions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {collisions.Count} column-name collision(s) under the default flatten (last value wins):");
            foreach (var group in collisions)
            {
                Console.WriteLine($"    {group.Key} <- {string.Join(", ", group.Select(p => p.Path))}");
            }

            Console.WriteLine("    disambiguate with columnMappings or a different separator.");
        }

        Console.WriteLine();
        Console.WriteLine("  value paths become columns; container/repeating paths are targets for root, keep-as-string,");
        Console.WriteLine("  exclude, or explode. A [*] path becomes a column when its repeat is exploded (one row per");
        Console.WriteLine("  element). Use --values to list only the column paths (one per line).");
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : value[..(width - 3)] + "...";

    private static void PrintDiscovery(string name, FlattenIntrospection introspection)
    {
        var inventory = introspection.Inventory;

        Console.WriteLine(
            $"Discovered {inventory.Paths.Count} path(s) for '{name}' across {inventory.RecordsScanned} record(s) in {inventory.FilesScanned} file(s).");

        if (introspection.AutoDetectedGrain is { } grain)
        {
            var label = introspection.SourceType == "xml" ? "rowXPath" : "rootPath";
            Console.WriteLine(
                $"  record grain auto-detected from sample statistics: {label} \"{grain}\" (one row per record); pass an explicit {label} to override.");
        }

        var drift = inventory.Paths.Any(p => p.RecordCount < inventory.RecordsScanned);
        if (drift)
        {
            Console.WriteLine("  schema drift: a path missing from a file becomes NULL for that file's rows (reconcile renames with pathAliases).");
        }

        if (inventory.Paths.Count == 0)
        {
            Console.WriteLine("  (no records found - check the location, file pattern, and the row path)");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"PATH",-50} {"COLUMN",-28} PRESENCE");
        foreach (var info in inventory.Paths)
        {
            var column = info.Kind == SchemaPathKind.Container ? "-" : info.Column;
            var presence = info.RecordCount >= inventory.RecordsScanned ? "all" : $"{info.RecordCount}/{inventory.RecordsScanned}";
            Console.WriteLine($"  {Truncate(info.Path, 50),-50} {Truncate(column, 28),-28} {presence}");
        }

        Console.WriteLine();
        Console.WriteLine("  Starter flatten config (paste under source.options):");
        foreach (var (key, value) in introspection.Options)
        {
            Console.WriteLine($"    {key}: \"{value}\"");
        }
    }

    /// <summary>
    /// Loads a flow and resolves a relative source location against the pipeline file's own directory, so
    /// a sample's <c>./data/x.json</c> works no matter which directory the command is run from (paths in a
    /// config file are naturally relative to that file). Absolute locations and non-file sources are left
    /// untouched.
    /// </summary>
    private static FlowDefinition LoadFlow(YamlFlowLoader loader, string file)
    {
        var flow = loader.LoadFile(file);
        return DocumentLoader.ResolveRelativeLocation(flow, file);
    }

    /// <summary>Runs a batch document: the orchestrator computes lineage waves over the members and runs them
    /// through the same <see cref="DocumentExecutor"/> a direct run uses. Writes the canonical batch artifacts
    /// (run.json, run.log, batch.json) and prints the per-wave outcome.</summary>
    private static async Task<int> RunBatchAsync(
        IServiceProvider provider, BatchFlow flow, string file, string[] args, bool json, CancellationToken ct = default)
    {
        RunParameters parameters;
        try
        {
            parameters = ParseRunParameters(args);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {ex.Message}");
            return 1;
        }

        var orchestrator = new BatchOrchestrator(provider.GetRequiredService<DocumentExecutor>());
        var memberOptions = new DocumentExecutionOptions
        {
            LogLevel = ParseLogLevel(GetOption(args, "--log-level")),
            ScmPush = !args.Contains("--no-push"),
            ScmDryRun = args.Contains("--dry-run"),
            // Members log to their own run folders; the console stays readable under concurrency.
            Echo = null,
            // The backfill parameters apply to EVERY member: a batch backfill is one command, not N YAML edits.
            Parameters = parameters,
        };

        BatchRunResult result;
        try
        {
            result = await orchestrator
                .RunAsync(flow, file, provider.GetRequiredService<ISecretResolver>(), memberOptions, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "CANCELLED  the batch was interrupted (Ctrl+C); the in-flight member's statement was aborted and rolled back.");
            return 130;
        }

        var runDirectory = RunHistory.Write(file, flow.SysAlias, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(new RunArtifact
            {
                FlowKind = "batch",
                FlowName = flow.SysAlias,
                RunId = result.RunId,
                Success = result.Success,
                WrittenUtc = DateTime.UtcNow,
                Error = result.Error,
                Result = result,
            }, ExecutionJson.Options),
            ["run.log"] = RunLogRenderer.RenderBatchLog(result),
            // A batch generates no SQL of its own; each member's trace is in its own run folder.
            ["trace.sql"] = string.Empty,
            ["batch.json"] = JsonSerializer.Serialize(result, ExecutionJson.Options),
        }, Console.Error.WriteLine);

        // Self-maintaining shadow: record the batch run AND each member run that executed (members carry their
        // own run.json under their folders); member.File is relative to the batch document's directory.
        var batchDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        var runs = result.Members
            .Where(m => m.RunDirectory is not null)
            .Select(m => (FlowFile: Path.Combine(batchDir, m.File), m.RunDirectory))
            .Append((FlowFile: file, RunDirectory: runDirectory))
            .ToList();
        await RecordRunsInCatalogAsync(provider, args, json, file, runs).ConfigureAwait(false);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, ExecutionJson.Options));
        }
        else
        {
            PrintBatchResult(result);
            if (runDirectory is not null)
            {
                Console.WriteLine($"  run log: {runDirectory}");
            }
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>Prints the per-kind summary of one executed document, then its run-log location.</summary>
    private static void PrintExecution(FlowDocument document, DocumentExecutionResult exec)
    {
        switch (document)
        {
            case FileFlowDocument:
                PrintResult((FlowResult)exec.Result);
                break;
            // An ingestion document can also have executed its embedded health check (--health-check); the
            // executor's FlowKind says which flow actually ran, so print by that, not by the document type.
            case IngestionFlowDocument when exec.FlowKind == "hc":
                PrintHealthCheckResult((HealthCheckRunResult)exec.Result, exec.HealthCheckReport);
                break;
            case IngestionFlowDocument doc:
                PrintIngestionResult(doc.Document.Flow, (IngestionRunResult)exec.Result);
                break;
            case ExportFlowDocument:
                PrintExportResult((ExportRunResult)exec.Result);
                break;
            case StoredProcedureFlowDocument doc:
            {
                var result = (StoredProcedureRunResult)exec.Result;
                Console.WriteLine(result.Success
                    ? $"OK  EXEC {doc.Document.Flow.Procedure.QualifiedName} completed in {result.DurationSeconds}s"
                    : $"FAILED  {result.Error}");
                break;
            }

            case HealthCheckFlowDocument:
                PrintHealthCheckResult((HealthCheckRunResult)exec.Result, exec.HealthCheckReport);
                break;
            case InvokeFlowDocument:
            {
                var result = (InvokeResult)exec.Result;
                Console.WriteLine(result.Success
                    ? $"OK  invoke '{result.InvokeAlias}' completed in {result.DurationSeconds}s ({result.StandardOutput})"
                    : $"FAILED  {result.Error}");
                break;
            }

            case SourceControlFlowDocument:
                PrintSourceControlResult((SourceControlResult)exec.Result);
                break;
            case CalendarFlowDocument doc:
            {
                var result = (CalendarRunResult)exec.Result;
                if (!result.Success)
                {
                    Console.WriteLine($"FAILED  {result.Error}");
                    break;
                }

                var createdNote = result.TableCreated ? " (table created)" : string.Empty;
                Console.WriteLine(
                    $"OK  calendar {doc.Document.Flow.Table.QualifiedName}{createdNote}: {result.RowsGenerated} day(s) generated, " +
                    $"{result.RowsInserted} inserted, {result.RowsUpdated} updated, {result.RowsDeleted} deleted, " +
                    $"{result.ObservedDays} observed in {result.DurationSeconds}s");
                break;
            }

            case TranslateFlowDocument doc:
            {
                var result = (SqlFlow.Core.Translate.TranslateRunResult)exec.Result;
                if (!result.Success)
                {
                    Console.WriteLine($"FAILED  {result.Error}");
                    break;
                }

                var delivery = doc.Document.Flow.Invoke is null
                    ? string.Empty
                    : $", {result.RequestsSent} request(s) delivered" +
                      (result.RequestsSkipped > 0 ? $" ({result.RequestsSkipped} skipped)" : string.Empty);
                Console.WriteLine(
                    $"OK  translate '{doc.Document.Flow.SysAlias}': {result.TotalRows} row(s) -> {result.Documents} document(s) " +
                    $"in {result.Files.Count} file(s){delivery} in {result.DurationSeconds}s");
                foreach (var outputFile in result.Files)
                {
                    Console.WriteLine($"  {outputFile.Path}: {outputFile.Rows} document(s), {outputFile.Bytes} byte(s)");
                }

                break;
            }
        }

        // The file flow already prints its own trace via PrintResult; every other kind points at its run folder.
        if (exec.RunDirectory is not null && document is not FileFlowDocument)
        {
            Console.WriteLine($"  run log: {exec.RunDirectory}");
        }
    }

    private static int ExitCodeForExecution(FlowDocument document, DocumentExecutionResult exec, string[] args)
        => document is HealthCheckFlowDocument || exec.FlowKind == "hc"
            ? HealthCheckExitCode((HealthCheckRunResult)exec.Result, args.Contains("--fail-on-anomaly"))
            : exec.Success ? 0 : 1;

    private static void PrintBatchResult(BatchRunResult result)
    {
        if (result.Error is not null && result.Waves.Count == 0)
        {
            Console.WriteLine($"FAILED  {result.Error}");
            return;
        }

        var status = result.Success ? "OK" : "FAILED";
        var manual = result.Manual > 0 ? $", {result.Manual} manual" : string.Empty;
        Console.WriteLine(
            $"{status}  batch '{result.BatchName}': {result.Waves.Count} wave(s); " +
            $"{result.Succeeded} succeeded, {result.Failed} failed, {result.Skipped} skipped, {result.Inactive} inactive{manual} (onError {result.OnError.ToLowerInvariant()}) in {result.DurationSeconds}s.");

        foreach (var wave in result.Waves)
        {
            Console.WriteLine($"  wave {wave.Wave}: {string.Join(", ", wave.Members)}");
        }

        foreach (var member in result.Members.Where(m => m.Status is BatchMemberStatus.Failed or BatchMemberStatus.FailedIgnored or BatchMemberStatus.Skipped or BatchMemberStatus.Manual))
        {
            var label = member.Status switch
            {
                BatchMemberStatus.Failed => "FAILED",
                BatchMemberStatus.FailedIgnored => "FAILED (ignored)",
                BatchMemberStatus.Manual => "MANUAL (not run; trigger it directly)",
                _ => "SKIPPED",
            };
            Console.WriteLine($"  {label}  {member.FlowName}{(member.Error is null ? string.Empty : $": {member.Error}")}");
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"  WARN  {warning}");
        }
    }

    private static void PrintSourceControlResult(SourceControlResult result)
    {
        if (!result.Success)
        {
            Console.WriteLine($"FAILED  {result.Error}");
            return;
        }

        var commit = result.DryRun
            ? "dry run (no commit)"
            : result.Committed
                ? $"committed {result.CommitSha?[..Math.Min(10, result.CommitSha.Length)]}{(result.Pushed ? " and pushed" : string.Empty)}"
                : "no change to commit";
        Console.WriteLine(
            $"OK  '{result.DatabaseName}': {result.ObjectsScripted} object(s) scripted; " +
            $"{result.Added} added, {result.Changed} changed, {result.Deleted} deleted ({commit}) in {result.DurationSeconds}s.");
        Console.WriteLine($"  repository: {result.WorkingDirectory} [{result.Branch}]{(result.Remote is null ? string.Empty : $" -> {result.Remote}")}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"  WARN  {warning}");
        }
    }

    private static RunLogLevel ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "info" => RunLogLevel.Info,
        "debug" => RunLogLevel.Debug,
        "trace" => RunLogLevel.Trace,
        _ => throw new SqlFlowException($"Unknown --log-level '{value}'. Allowed: info, debug, trace."),
    };

    /// <summary>
    /// The built-in backfill's CLI surface: <c>--full</c> ignores the watermark, <c>--from</c>/<c>--to</c> is an
    /// externally-bounded window, <c>--file-pattern</c> narrows a file flow to one glob, and
    /// <c>--assertions-only</c> evaluates an ingestion flow's assertions (manual-mode ones included) against the
    /// current target without loading anything. Parsed and validated here (dates are invariant-culture, e.g.
    /// <c>2023-01-15</c> or <c>2023-01-15 06:00:00</c>), the same <see cref="RunParameters"/> contract the
    /// control-plane trigger validates, so both entry points refuse exactly the same nonsense. A batch run
    /// passes them to every member.
    /// </summary>
    internal static RunParameters ParseRunParameters(string[] args)
    {
        static DateTime? ParseDate(string? value, string flag)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }

            throw new SqlFlowException($"{flag} '{value}' is not a date; use e.g. 2023-01-15 or '2023-01-15 06:00:00'.");
        }

        var parameters = new RunParameters
        {
            FullLoad = args.Contains("--full"),
            BackfillFrom = ParseDate(GetOption(args, "--from"), "--from"),
            BackfillTo = ParseDate(GetOption(args, "--to"), "--to"),
            FilePattern = GetOption(args, "--file-pattern"),
            AssertionsOnly = args.Contains("--assertions-only"),
            SourceFilter = GetOption(args, "--source-filter"),
        };
        parameters.Validate();
        return parameters;
    }

    /// <summary>0 on success, 1 on failure, 2 when --fail-on-anomaly was set and a mature anomaly exists
    /// (the CI gate: distinguishable from a broken run).</summary>
    private static int HealthCheckExitCode(HealthCheckRunResult result, bool failOnAnomaly)
        => !result.Success ? 1 : failOnAnomaly && result.TotalAnomalies > 0 ? 2 : 0;

    private static void PrintHealthCheckResult(HealthCheckRunResult result, HealthCheckReport? report)
    {
        Console.WriteLine(result.Success
            ? $"OK  {result.TotalAnomalies} anomalies across {result.MetricResults.Count} metric(s) ({result.Frequency}) in {result.DurationSeconds}s"
            : $"FAILED  {result.Error}");

        if (result.DataQuality is { } q && (q.FutureDatedRows > 0 || q.SentinelDatedRows > 0 || q.NullDatedRows > 0))
        {
            Console.WriteLine($"  data quality: {q.FutureDatedRows} future-dated, {q.SentinelDatedRows} sentinel-dated, {q.NullDatedRows} NULL-dated row(s)");
        }

        foreach (var metric in result.MetricResults)
        {
            if (metric.Error is not null)
            {
                Console.WriteLine($"  {metric.Name}: ERROR  {metric.Error}");
                continue;
            }

            Console.WriteLine(
                $"  {metric.Name}: {metric.Anomalies} anomalies in {metric.SeriesPoints} point(s) " +
                $"({metric.ImputedPoints} imputed, {metric.ImmaturePoints} immature)" +
                $"{(metric.LevelShifts > 0 ? $", {metric.LevelShifts} level shift(s)" : string.Empty)}" +
                $"; model {metric.ModelTrainer} ({(metric.ModelTrained ? "trained" : "reused")})" +
                (metric.Fit is { } fit ? $", R2 {fit.RSquared:0.###}" : string.Empty));
        }

        if (report is null)
        {
            return;
        }

        // The findings themselves, worst first: this is the part an operator acts on.
        foreach (var metric in report.Metrics)
        {
            foreach (var shift in metric.LevelShifts)
            {
                Console.WriteLine(
                    $"  SHIFT    {metric.Name} {shift.Date:yyyy-MM-dd}: level {shift.MedianBefore:0.##} -> {shift.MedianAfter:0.##} ({shift.MagnitudeSigma:0.#} sigma)");
            }

            foreach (var point in metric.Series
                         .Where(p => p.Anomaly)
                         .OrderByDescending(p => p.Severity)
                         .Take(10))
            {
                Console.WriteLine(
                    $"  ANOMALY  {metric.Name} {point.Date:yyyy-MM-dd}: actual {point.Actual:0.##}, expected {point.Predicted:0.##} " +
                    $"({point.AnomalyReason}, severity {point.Severity:0.#})");
            }

            var hidden = metric.AnomalySummary.Total - Math.Min(10, metric.AnomalySummary.Total);
            if (hidden > 0)
            {
                Console.WriteLine($"           {metric.Name}: {hidden} more anomalies in healthcheck.json");
            }
        }
    }

    private static void PrintExportResult(ExportRunResult result)
    {
        Console.WriteLine(result.Success
            ? $"OK  exported {result.TotalRows} row(s) to {result.Files.Count} file(s) in {result.DurationSeconds}s"
            : $"FAILED  {result.Error}");
        foreach (var exported in result.Files)
        {
            Console.WriteLine($"  {exported.Path}  {exported.Rows} row(s), {exported.Bytes} byte(s)");
        }
    }

    private static void PrintIngestionResult(IngestionFlow flow, IngestionRunResult result)
    {
        var status = result.Success ? "OK" : "FAILED";
        var name = flow.SysAlias ?? flow.Target.Table.Name;
        Console.WriteLine(
            $"{status}  '{name}': {result.RowsStaged} row(s) staged, {result.RowsInserted} inserted, {result.RowsUpdated} updated in {result.DurationSeconds}s ({result.FlowRate:0.#} rows/s).");
        if (result.Error is not null)
        {
            Console.WriteLine($"  error: {result.Error}");
        }

        if (result.SourceWhere.Length > 0)
        {
            Console.WriteLine($"  incremental: WHERE 1=1{result.SourceWhere}");
        }

        if (result.StagingRetained)
        {
            Console.WriteLine($"  staging kept: {result.StagingTable}");
        }

        foreach (var action in result.IndexActions)
        {
            Console.WriteLine($"  index {action.IndexName}: {action.Kind}{(action.Detail is null ? string.Empty : $" ({action.Detail})")}");
        }

        foreach (var assertion in result.Assertions)
        {
            var outcome = assertion.Error is not null ? $"error: {assertion.Error}"
                : assertion.Evaluated ? $"result: {assertion.Result}"
                : "skipped";
            Console.WriteLine($"  assertion {assertion.Name}: {outcome}");
        }

        foreach (var key in result.SurrogateKeys)
        {
            var outcome = key.Error is not null ? $"error: {key.Error}"
                : $"{key.KeysGenerated} key(s) generated, {key.RowsStamped} row(s) stamped";
            Console.WriteLine($"  surrogate key {key.SurrogateTable}: {outcome}");
        }
    }

    /// <summary>Every option that consumes the next token as its value. Kept in sync with the GetOption /
    /// ParseIntOption / MapOption call sites so an option's value is never mistaken for a positional argument
    /// (the command and the file), regardless of where the user places the option.</summary>
    internal static readonly HashSet<string> ValueTakingOptions = new(StringComparer.Ordinal)
    {
        "-o", "--out", "--log-level",
        "--max-files", "--max-records", "--max-depth",
        "--source", "--target", "--database", "--schema", "--target-schema", "--provider",
        "--like", "--offset", "--limit", "--term", "--object", "--target-object", "--keys", "--name",
        "--pattern", "--root", "--keep", "--include", "--exclude", "--explode", "--aliases",
        "--separator", "--join-separator", "--map", "--array", "--repeat", "--xml",
        "--date-column", "--base-value", "--filter", "--threshold", "--alpha", "--budget", "--maturity", "--state-dir",
        "--of", "--explain",
        "--db", "--repo", "--repo-url",
        // The control-plane verbs (health/login/logout/trigger/runs/groups and the estate family).
        "--url", "--token", "--username", "--token-name", "--expires-days", "--scopes",
        "--scope", "--batch", "--pool", "--poll-seconds", "--commit", "--flow", "--status", "--kind", "--group",
        "--page", "--page-size", "--from", "--to", "--file-pattern", "--source-filter",
        "--cron", "--interval", "--timezone", "--max-concurrency",
        "--remote-url", "--credential-ref", "--credential-user",
        "--ref", "--sample", "--max-columns", "--max-candidates", "--active", "--enabled",
        "--search", "--relation", "--tier", "--server", "--operation", "--last",
    };

    internal static string[] PositionalArguments(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (ValueTakingOptions.Contains(args[i]))
                {
                    i++;
                }

                continue;
            }

            positional.Add(args[i]);
        }

        return [.. positional];
    }

    internal static string? GetOption(string[] args, params string[] names)
    {
        var index = Array.FindIndex(args, a => names.Contains(a));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        // A value-taking flag with no value would otherwise swallow the next flag (e.g. `--explode --data`
        // setting explodePaths to "--data"). A lone "-" is still allowed (e.g. a separator).
        var value = args[index + 1];
        return value.Length > 1 && value[0] == '-' ? null : value;
    }

    internal static int ParseIntOption(string[] args, int fallback, params string[] names)
    {
        var value = GetOption(args, names);
        return value is not null && int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
    }
}
