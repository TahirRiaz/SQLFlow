using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Orchestration;
using SqlFlow.Azure;
using SqlFlow.Azure.Invoke;
using SqlFlow.HealthCheck;
using SqlFlow.Providers;
using SqlFlow.SourceControl;
using SqlFlow.SqlServer.Calendar;
using SqlFlow.SqlServer.Export;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.SqlServer.Invoke;
using SqlFlow.SqlServer.StoredProcedures;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>The full result of one document run: the uniform outcome plus the typed result and rendered SQL the
/// caller needs to print, --json, and --show-sql. The batch projects this down to <see cref="DocumentRunOutcome"/>.</summary>
public sealed record DocumentExecutionResult
{
    public required string FlowName { get; init; }
    public required string FlowKind { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public Guid RunId { get; init; }
    public string? RunDirectory { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>The kind-specific result object (FlowResult, IngestionRunResult, ...), for printing and --json.</summary>
    public required object Result { get; init; }

    /// <summary>The generated SQL, rendered, for --show-sql.</summary>
    public string SqlTraceText { get; init; } = string.Empty;

    /// <summary>The scored health-check report, for hc printing; null for every other kind.</summary>
    public HealthCheckReport? HealthCheckReport { get; init; }
}

/// <summary>
/// The single execution pathway for one flow document: it builds the kind's runner exactly as the without-database
/// composition roots prescribe, runs it, and writes the canonical run-history artifacts. The CLI's <c>run</c>
/// verb and the batch orchestrator both go through here, so a batch member runs through identical code to a
/// directly-invoked flow. The executor never writes to the console; the caller prints from the returned result, a
/// live run-log echo is opt-in through <see cref="DocumentExecutionOptions.Echo"/>, and the hygiene/history
/// warnings are surfaced through an optional sink supplied at construction.
/// </summary>
public sealed class DocumentExecutor : IDocumentRunner
{
    private readonly IServiceProvider _provider;
    private readonly Action<string>? _warningSink;

    /// <param name="provider">The composition root the runners and resolvers are pulled from.</param>
    /// <param name="warningSink">Where secret-hygiene and run-history warnings are surfaced; null suppresses them.
    /// The CLI wires this to standard error so a batch member warns exactly as a directly-invoked flow does.</param>
    public DocumentExecutor(IServiceProvider provider, Action<string>? warningSink = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _warningSink = warningSink;
    }

    /// <summary>The batch entry point: load the member document (with the same relative-path fixups and
    /// secret-hygiene check as a direct run), execute it, and project to the uniform outcome.</summary>
    public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        var documents = _provider.GetRequiredService<YamlDocumentLoader>();

        FlowDocument document;
        try
        {
            document = DocumentLoader.Load(documents, flowFile, _warningSink);
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
        {
            return new DocumentRunOutcome
            {
                FlowName = Path.GetFileName(flowFile),
                FlowKind = "unknown",
                Success = false,
                Error = SecretHygiene.RedactedMessage(ex),
            };
        }

        if (document is BatchFlowDocument)
        {
            return new DocumentRunOutcome
            {
                FlowName = Path.GetFileName(flowFile),
                FlowKind = "batch",
                Success = false,
                Error = "a batch cannot be a member of another batch.",
            };
        }

        var result = await ExecuteAsync(document, flowFile, options, ct).ConfigureAwait(false);
        return new DocumentRunOutcome
        {
            FlowName = result.FlowName,
            FlowKind = result.FlowKind,
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = result.RunDirectory,
            DurationSeconds = result.DurationSeconds,
        };
    }

    /// <summary>Runs an already-loaded document and writes its artifacts, returning the rich result.</summary>
    public async Task<DocumentExecutionResult> ExecuteAsync(
        FlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Unlike the advisory backfill parameters (which flow kinds without a window surface as a run-log
        // notice), assertionsOnly changes WHAT the run does: honoring it on a kind without assertions would
        // silently run a full load the caller explicitly did not ask for, so any kind but ingestion refuses.
        if (options.Parameters.AssertionsOnly && document is not IngestionFlowDocument)
        {
            throw new SqlFlowException(
                "assertionsOnly applies only to ingestion flows (flowType: ing): assertions are declared on and " +
                "evaluated against an ingestion flow's target.");
        }

        // An ingestion document with an embedded healthCheck: block expands into two pipelines sharing one
        // file: the load and its derived hc sibling. The caller selects by flow name (the node worker passes
        // the claimed run's name, a batch each member's, the CLI's --health-check the derived name); the
        // derived check then executes through the exact same hc path as a standalone document, monitoring the
        // flow's target through the document's own connections. A name matching neither flow is refused loudly:
        // silently running the load when the caller asked for something else would be the worst outcome.
        if (document is IngestionFlowDocument ingestion && options.FlowName is { } requestedFlow)
        {
            var embedded = ingestion.Document.HealthCheck;
            if (embedded is not null && string.Equals(requestedFlow, embedded.SysAlias, StringComparison.OrdinalIgnoreCase))
            {
                if (options.Parameters.AssertionsOnly)
                {
                    throw new SqlFlowException(
                        $"assertionsOnly applies to the ingestion flow, not its embedded health check '{embedded.SysAlias}'.");
                }

                // The document's schedule: block belongs to the ingestion flow; the derived check carries none.
                var derived = new HealthCheckFlowDocument
                {
                    Document = new HealthCheckDocument { Flow = embedded, Connections = ingestion.Document.Connections },
                };
                return await ExecuteHealthCheckAsync(derived, flowFile, options, ct).ConfigureAwait(false);
            }

            var primaryName = ingestion.Document.Flow.SysAlias ?? ingestion.Document.Flow.Target.Table.Name;
            if (!string.Equals(requestedFlow, primaryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlFlowException(
                    $"This document declares flow '{primaryName}'" +
                    (embedded is null ? string.Empty : $" and embedded health check '{embedded.SysAlias}'") +
                    $", not '{requestedFlow}'. The file and the catalog have drifted; re-sync the repo.");
            }
        }

        return document switch
        {
            FileFlowDocument doc => await ExecuteFileAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            IngestionFlowDocument doc => await ExecuteIngestionAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            ExportFlowDocument doc => await ExecuteExportAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            StoredProcedureFlowDocument doc => await ExecuteStoredProcedureAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            HealthCheckFlowDocument doc => await ExecuteHealthCheckAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            InvokeFlowDocument doc => await ExecuteInvokeAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            AcquireFlowDocument doc => await ExecuteAcquireAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            CopyFlowDocument doc => await ExecuteCopyAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            SftpFlowDocument doc => await ExecuteSftpAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            SourceControlFlowDocument doc => await ExecuteSourceControlAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            CalendarFlowDocument doc => await ExecuteCalendarAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            TranslateFlowDocument doc => await ExecuteTranslateAsync(doc, flowFile, options, ct).ConfigureAwait(false),
            _ => throw new SqlFlowException($"Cannot run document kind '{document.GetType().Name}'."),
        };
    }

    private async Task<DocumentExecutionResult> ExecuteFileAsync(FileFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var runner = _provider.GetRequiredService<FlowRunner>();
        var flow = ApplyFileRunParameters(doc.Flow, options.Parameters);

        // The file flow publishes its canonical events (file reads, watermark decisions, stage summaries)
        // natively; the collector rides alongside the host-wide sink so they land in run.json and, when the node
        // attached a live sink, in the catalog while the run executes.
        var events = new RunEventCollector(options.EventSink);

        // The run-history anchor (the flow document's folder) is the same one RunHistory.Write uses below, so the
        // incremental probe reads the durable last-processed watermark from exactly the runs written here.
        var runHistoryDirectory = Path.GetDirectoryName(Path.GetFullPath(flowFile)) ?? Directory.GetCurrentDirectory();
        var result = await runner.RunAsync(flow, options.RunId, options.StatementSink, runHistoryDirectory, options.WatermarkSourceTable, events, options.LandingReset, ct).ConfigureAwait(false);
        var trace = SqlTrace.Render(result.SqlTrace);

        var runDirectory = RunHistory.Write(flowFile, doc.Flow.Name, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("file", doc.Flow.Name, result.RunId, result.Status == FlowStatus.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = RunLogRenderer.RenderFileFlowLog(result),
            ["trace.sql"] = trace,
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = doc.Flow.Name,
            FlowKind = "file",
            Success = result.Status == FlowStatus.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = Math.Round(result.TotalMs / 1000.0, 3),
            Result = result,
            SqlTraceText = trace,
        };
    }

    /// <summary>A run-log notice for flow kinds that have no window/selection surface, so supplied parameters
    /// are visibly acknowledged rather than silently dropped (the run detail's log answers "did my backfill
    /// apply?" definitively).</summary>
    private static void NoteInapplicableParameters(IRunEventSink events, RunParameters parameters, string flowKind)
    {
        if (!parameters.IsDefault)
        {
            events.Log(RunLogLevel.Info, "parameters",
                $"run parameters '{parameters.Describe()}' do not apply to flowType '{flowKind}'; the flow runs as defined.");
        }
    }

    /// <summary>Builds the per-run event plumbing shared by every run-log-driven flow kind: the canonical
    /// <see cref="RunLogger"/> (rendered to <c>run.log</c>), the collector whose records become the run.json
    /// <c>events</c> array (forwarding live to the node's sink when one is attached), and the bridge the runner
    /// logs through so one <c>Log</c> call feeds both.</summary>
    private static (RunLogger RunLogger, RunEventCollector Events, RunLogEventBridge Sink) BuildEventPlumbing(
        DocumentExecutionOptions options, string flowName)
    {
        var runLogger = new RunLogger(options.LogLevel, options.Echo);
        var events = new RunEventCollector(options.EventSink);
        return (runLogger, events, new RunLogEventBridge(runLogger, events, options.RunId, flowName));
    }

    /// <summary>
    /// Applies the per-run substitution parameters to a file flow by rewriting the SAME knobs the definition
    /// itself uses (the source options and the incremental spec), so the engine needs no second code path:
    /// <list type="bullet">
    /// <item>a backfill window becomes the init file-date window (<c>initFromFileDate</c>/<c>initToFileDate</c>),
    /// the engine's native externally-bounded selection;</item>
    /// <item><c>FilePattern</c> becomes <c>srcFile</c>, narrowing discovery to the requested glob;</item>
    /// <item>full load, or an explicit window, suppresses the watermark probe (<c>Incremental.FullLoad</c>),
    /// because an explicit bound must never be narrowed further by the target's state.</item>
    /// </list>
    /// A run with default parameters returns the flow unchanged.
    /// </summary>
    private static FlowDefinition ApplyFileRunParameters(FlowDefinition flow, RunParameters parameters)
    {
        if (parameters.IsDefault)
        {
            return flow;
        }

        var options = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase);
        if (parameters.BackfillFrom is { } from)
        {
            options["initFromFileDate"] = from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (parameters.BackfillTo is { } to)
        {
            options["initToFileDate"] = to.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(parameters.FilePattern))
        {
            options["srcFile"] = parameters.FilePattern;
        }

        var incremental = flow.Incremental;
        if (incremental is not null && (parameters.FullLoad || parameters.BackfillFrom is not null))
        {
            incremental = incremental with { FullLoad = true };
        }

        return flow with
        {
            Source = flow.Source with { Options = options },
            Incremental = incremental,
        };
    }

    private async Task<DocumentExecutionResult> ExecuteIngestionAsync(IngestionFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var flowName = doc.Document.Flow.SysAlias ?? doc.Document.Flow.Target.Table.Name;
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, flowName);
        var runner = WithoutDatabaseIngestion.BuildRunner(
            doc.Document.Connections,
            doc.Document.AssertionDefinitions,
            _provider.GetRequiredService<ISecretResolver>(),
            SqlFlowSourceProviders.CreateRegistry(),
            doc.Document.Invokes,
            InvokeExecutorFactory.Create(_provider, doc.Document.Invokes, doc.Document.ServicePrincipals));
        var result = await runner.RunAsync(
            doc.Document.Flow,
            new IngestionRunOptions
            {
                ExecMode = "cli", Events = eventSink, RunId = options.RunId, Parameters = options.Parameters,
                StatementSink = options.StatementSink, WatermarkSourceTable = options.WatermarkSourceTable,
            },
            ct).ConfigureAwait(false);

        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("ing", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "ing",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
            SqlTraceText = SqlTrace.Render(result.SqlTrace),
        };
    }

    private async Task<DocumentExecutionResult> ExecuteExportAsync(ExportFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, doc.Document.Flow.SysAlias);
        var runner = WithoutDatabaseExport.BuildRunner(
            doc.Document.Connections,
            _provider.GetRequiredService<ISecretResolver>(),
            doc.Document.Invokes,
            InvokeExecutorFactory.Create(_provider, doc.Document.Invokes, doc.Document.ServicePrincipals),
            // Local plus the Azure blob/ADLS destination. The runner selects by CanHandle, so a local path stays
            // local and an abfss/https storage path writes to the lake, authenticated by the shared Azure
            // credential (SQLFLOW_AZURE_AUTH) with no per-flow secret.
            destinations: [new LocalExportDestination(), new AzureBlobExportDestination(_provider.GetRequiredService<IAzureCredentialFactory>())]);

        // Per-run substitution: a backfill window re-windows the export's chunk plan (its native FromDate/ToDate
        // bounds) for THIS run only. FullLoad has no export meaning (the planner's window IS the selection), and
        // a file pattern is a source concern, so both are surfaced as an explicit notice rather than ignored.
        var exportFlow = doc.Document.Flow;
        if (options.Parameters.BackfillFrom is { } exportFrom)
        {
            exportFlow = exportFlow with
            {
                FromDate = DateOnly.FromDateTime(exportFrom),
                ToDate = options.Parameters.BackfillTo is { } exportTo ? DateOnly.FromDateTime(exportTo) : exportFlow.ToDate,
            };
            eventSink.Log(RunLogLevel.Info, "parameters", $"export window overridden by run parameters: {options.Parameters.Describe()}");
        }
        else if (!options.Parameters.IsDefault)
        {
            eventSink.Log(RunLogLevel.Info, "parameters",
                $"run parameters '{options.Parameters.Describe()}' do not apply to an export flow (only a backfill window does); running as defined.");
        }

        var result = await runner.RunAsync(exportFlow, new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, StatementSink = options.StatementSink }, ct).ConfigureAwait(false);

        var flowName = doc.Document.Flow.SysAlias;
        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("exp", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "exp",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
            SqlTraceText = SqlTrace.Render(result.SqlTrace),
        };
    }

    private async Task<DocumentExecutionResult> ExecuteTranslateAsync(TranslateFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var flowName = doc.Document.Flow.SysAlias;
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, flowName);
        NoteInapplicableParameters(eventSink, options.Parameters, "trl");
        var runner = SqlFlow.Translate.WithoutDatabaseTranslate.BuildRunner(
            doc.Document.Connections,
            _provider.GetRequiredService<ISecretResolver>(),
            // Local plus the Azure blob/ADLS destination, exactly as the export flow composes them: the runner
            // selects by CanHandle, and the delivery step reads the saved files back through the same seam.
            destinations: [new LocalExportDestination(), new AzureBlobExportDestination(_provider.GetRequiredService<IAzureCredentialFactory>())]);
        var result = await runner.RunAsync(
            doc.Document.Flow,
            new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, StatementSink = options.StatementSink },
            ct).ConfigureAwait(false);

        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("trl", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "trl",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
            SqlTraceText = SqlTrace.Render(result.SqlTrace),
        };
    }

    private async Task<DocumentExecutionResult> ExecuteStoredProcedureAsync(StoredProcedureFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, doc.Document.Flow.SysAlias);
        NoteInapplicableParameters(eventSink, options.Parameters, "sp");
        var runner = WithoutDatabaseStoredProcedure.BuildRunner(
            doc.Document.Connections,
            _provider.GetRequiredService<ISecretResolver>(),
            doc.Document.Invokes,
            InvokeExecutorFactory.Create(_provider, doc.Document.Invokes, doc.Document.ServicePrincipals));
        var result = await runner.RunAsync(doc.Document.Flow, new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, StatementSink = options.StatementSink }, ct).ConfigureAwait(false);

        var flowName = doc.Document.Flow.SysAlias;
        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("sp", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "sp",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
            SqlTraceText = SqlTrace.Render(result.SqlTrace),
        };
    }

    private async Task<DocumentExecutionResult> ExecuteHealthCheckAsync(HealthCheckFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, doc.Document.Flow.SysAlias);
        NoteInapplicableParameters(eventSink, options.Parameters, "hc");
        var anchor = Path.GetDirectoryName(Path.GetFullPath(flowFile)) ?? Directory.GetCurrentDirectory();
        var runner = WithoutDatabaseHealthCheck.BuildRunner(
            doc.Document.Connections, anchor, _provider.GetRequiredService<ISecretResolver>());
        var flow = options.Retrain ? doc.Document.Flow with { Training = HealthCheckTraining.Always } : doc.Document.Flow;

        var outcome = await runner.RunAsync(flow, new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, StatementSink = options.StatementSink }, ct: ct).ConfigureAwait(false);
        var result = outcome.Result;

        var flowName = flow.SysAlias;
        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("hc", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
            ["healthcheck.json"] = outcome.Report is null ? string.Empty : JsonSerializer.Serialize(outcome.Report, ExecutionJson.Options),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "hc",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
            SqlTraceText = SqlTrace.Render(result.SqlTrace),
            HealthCheckReport = outcome.Report,
        };
    }

    private async Task<DocumentExecutionResult> ExecuteCalendarAsync(CalendarFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, doc.Document.Flow.SysAlias);
        NoteInapplicableParameters(eventSink, options.Parameters, "cal");
        var runner = WithoutDatabaseCalendar.BuildRunner(
            doc.Document.Connections, _provider.GetRequiredService<ISecretResolver>());
        var result = await runner.RunAsync(
            doc.Document.Flow,
            new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, StatementSink = options.StatementSink },
            ct).ConfigureAwait(false);

        var flowName = doc.Document.Flow.SysAlias;
        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("cal", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = SqlTrace.Render(result.SqlTrace),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "cal",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
            SqlTraceText = SqlTrace.Render(result.SqlTrace),
        };
    }

    private async Task<DocumentExecutionResult> ExecuteInvokeAsync(InvokeFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, doc.Document.Definition.InvokeAlias);
        NoteInapplicableParameters(eventSink, options.Parameters, "inv");
        var runner = WithoutDatabaseInvoke.BuildRunner(
            AzureInvokeExecutors.Create(
                new SqlFlow.Core.Connections.InMemoryServicePrincipalStore(doc.Document.ServicePrincipals),
                _provider.GetRequiredService<ISecretResolver>(),
                _provider.GetRequiredService<IAzureCredentialFactory>()));
        var result = await runner.RunAsync(doc.Document.Definition, new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId }, ct).ConfigureAwait(false);

        var flowName = doc.Document.Definition.InvokeAlias;
        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("inv", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = string.Empty,
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "inv",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
        };
    }

    private async Task<DocumentExecutionResult> ExecuteAcquireAsync(AcquireFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var flowName = doc.Flow.Name;
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, flowName);

        // A backfill window re-windows every date-window iteration and a full load ignores the stored watermark
        // (both handled by the acquire runner, which receives the typed parameters below); a file pattern has no
        // acquisition meaning, so it alone is surfaced as an explicit notice rather than silently dropped.
        if (!string.IsNullOrWhiteSpace(options.Parameters.FilePattern))
        {
            eventSink.Log(RunLogLevel.Info, "parameters",
                "run parameter 'filePattern' does not apply to an acquisition flow (flowType: acq); the flow runs as defined.");
        }

        // Incremental resume reads the run history anchored at the flow document's folder, the same anchor
        // RunHistory.Write uses below, so a resumed run reads exactly the watermarks written here.
        var anchor = Path.GetDirectoryName(Path.GetFullPath(flowFile)) ?? Directory.GetCurrentDirectory();
        var runner = _provider.GetRequiredService<SqlFlow.Acquire.Engine.AcquireFlowRunner>();
        var result = await runner.RunAsync(
            doc.Flow, anchor,
            new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, Parameters = options.Parameters },
            ct).ConfigureAwait(false);

        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("api", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = string.Empty,
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "api",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
        };
    }

    private async Task<DocumentExecutionResult> ExecuteCopyAsync(CopyFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var flowName = doc.Flow.Name;
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, flowName);

        var runner = _provider.GetRequiredService<SqlFlow.Copy.CopyFlowRunner>();
        var result = await runner.RunAsync(
            doc.Flow,
            new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, Parameters = options.Parameters },
            ct).ConfigureAwait(false);

        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("cpy", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = string.Empty,
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "cpy",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
        };
    }

    private async Task<DocumentExecutionResult> ExecuteSftpAsync(SftpFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var flowName = doc.Flow.Name;
        var (runLogger, events, eventSink) = BuildEventPlumbing(options, flowName);

        var runner = _provider.GetRequiredService<SqlFlow.Sftp.SftpFlowRunner>();
        var result = await runner.RunAsync(
            doc.Flow,
            new IngestionRunOptions { ExecMode = "cli", Events = eventSink, RunId = options.RunId, Parameters = options.Parameters },
            ct).ConfigureAwait(false);

        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(Artifact("sftp", flowName, result.RunId, result.Success, result.Error, result, events.Records), ExecutionJson.Options),
            ["run.log"] = runLogger.Render(),
            ["trace.sql"] = string.Empty,
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "sftp",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
        };
    }

    private async Task<DocumentExecutionResult> ExecuteSourceControlAsync(SourceControlFlowDocument doc, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        var flow = ResolveRelativeRepositoryPath(doc.Document.Flow, flowFile);
        var service = WithoutDatabaseSourceControl.BuildService(doc.Document.Connections, _provider.GetRequiredService<ISecretResolver>());

        // The snapshot narrates itself onto the run's canonical event stream, exactly as every other kind does:
        // the collector forwards each event live (the node streams it into the catalog, which is what the trace
        // panel tails) and keeps the records for run.json. Without this a run that walks thousands of objects
        // for minutes shows nothing until it is over.
        var events = new RunEventCollector(options.EventSink);
        var result = await service.RunAsync(
            flow,
            new SourceControlRunOptions
            {
                DryRun = options.ScmDryRun,
                Push = options.ScmPush,
                RunId = options.RunId,
                Events = events,
            },
            ct).ConfigureAwait(false);

        var flowName = flow.SysAlias;
        var runDirectory = RunHistory.Write(flowFile, flowName, result.RunId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(
                Artifact("scm", flowName, result.RunId, result.Success, result.Error, result, events.Records),
                ExecutionJson.Options),
            ["run.log"] = RunLogRenderer.RenderSourceControlLog(result),
            ["trace.sql"] = string.Empty,
            ["scm.json"] = JsonSerializer.Serialize(result, ExecutionJson.Options),
        }, _warningSink);

        return new DocumentExecutionResult
        {
            FlowName = flowName,
            FlowKind = "scm",
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = runDirectory,
            DurationSeconds = result.DurationSeconds,
            Result = result,
        };
    }

    private static RunArtifact Artifact(
        string kind, string flowName, Guid runId, bool success, string? error, object result,
        IReadOnlyList<RunEventRecord>? events = null)
        => new()
        {
            FlowKind = kind,
            FlowName = flowName,
            RunId = runId,
            Success = success,
            WrittenUtc = DateTime.UtcNow,
            Error = error,
            Result = result,
            Events = events ?? [],
        };

    private static SqlFlow.Core.SourceControl.SourceControlFlow ResolveRelativeRepositoryPath(SqlFlow.Core.SourceControl.SourceControlFlow flow, string file)
    {
        var path = flow.Repository.WorkingDirectory;
        if (path.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(path))
        {
            return flow;
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory();
        var resolved = Path.GetFullPath(Path.Combine(baseDir, path));
        return flow with { Repository = flow.Repository with { WorkingDirectory = resolved } };
    }
}
