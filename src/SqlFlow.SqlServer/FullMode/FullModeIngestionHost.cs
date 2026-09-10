using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.StoredProcedures;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Export;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.SqlServer.Invoke;
using SqlFlow.SqlServer.StoredProcedures;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>Options for constructing a <see cref="FullModeIngestionHost"/>.</summary>
public sealed record FullModeOptions
{
    /// <summary>The control-database connection (where flw.DataSource / flw.Assertion / flw.SysLog live). The
    /// run log and the registry/assertion stores all read and write here.</summary>
    public required string ControlConnectionString { get; init; }

    /// <summary>The secret resolver that expands <c>${env:..}</c> / <c>${keyvault:..}</c> references in a
    /// data source's ConnectionRef. Defaults to an environment-variable resolver; inject a Key-Vault-backed one
    /// in production.</summary>
    public ISecretResolver? Secrets { get; init; }

    /// <summary>Run data-quality assertions from flw.Assertion (default true).</summary>
    public bool EnableAssertions { get; init; } = true;

    /// <summary>Generate surrogate keys from flw.SurrogateKey and write them back (default true).</summary>
    public bool EnableSurrogateKeys { get; init; } = true;

    /// <summary>Write the per-run record to flw.SysLog / flw.SysStats (default true).</summary>
    public bool EnableRunLog { get; init; } = true;

    /// <summary>Honor the Pre/PostInvokeAlias hooks on ingestion, stored-procedure, and export flows by running
    /// the referenced flw.Invoke flow (default true). When false, a set hook alias is surfaced as a clear error
    /// rather than run; the standalone invoke entry points (RunInvokeByIdAsync and the like) work regardless.</summary>
    public bool EnableInvoke { get; init; } = true;

    /// <summary>The invoke executors registered behind the dispatcher (in production an Azure Data Factory and
    /// an Azure Automation executor, built by the composition root from SqlFlow.Azure). Empty by default, which
    /// keeps SqlFlow.SqlServer free of any Azure dependency: a set invoke then fails with a clear "no executor
    /// registered" result. The host only ever sees the Core <see cref="IInvokeExecutor"/> abstraction.</summary>
    public IReadOnlyList<IInvokeExecutor> InvokeExecutors { get; init; } = [];

    /// <summary>Additional source providers (MySQL, PostgreSQL) appended to the built-in SQL Server set, built
    /// by the composition root from SqlFlow.Providers so this assembly never references the provider SDKs.</summary>
    public SourceProviderRegistry? SourceProviders { get; init; }
}

/// <summary>
/// The full-mode (with control database) composition root: the single production constructor of a fully-wired
/// <see cref="IngestionFlowRunner"/>. It swaps each without-database <c>Null*</c> seam for its SQL-backed
/// implementation (the alias-resolving data-source store, the run log, the assertion runner) without changing
/// the runner, and exposes load-and-run entry points by flow id, SysAlias, or batch. Without-database mode is
/// untouched: it simply never constructs this host.
/// </summary>
public sealed class FullModeIngestionHost
{
    private readonly StoredProcedureFlowRunner _storedProcedureRunner;
    private readonly ExportFlowRunner _exportRunner;
    private readonly InvokeFlowRunner _invokeRunner;

    private FullModeIngestionHost(
        IngestionFlowRunner runner,
        IFlowLoader flows,
        IDataSourceStore dataSources,
        StoredProcedureFlowRunner storedProcedureRunner,
        IStoredProcedureFlowLoader storedProcedureFlows,
        ExportFlowRunner exportRunner,
        IExportFlowLoader exportFlows,
        InvokeFlowRunner invokeRunner,
        IInvokeFlowLoader invokeFlows)
    {
        Runner = runner;
        Flows = flows;
        DataSources = dataSources;
        _storedProcedureRunner = storedProcedureRunner;
        StoredProcedureFlows = storedProcedureFlows;
        _exportRunner = exportRunner;
        ExportFlows = exportFlows;
        _invokeRunner = invokeRunner;
        InvokeFlows = invokeFlows;
    }

    public IngestionFlowRunner Runner { get; }

    public IFlowLoader Flows { get; }

    public IStoredProcedureFlowLoader StoredProcedureFlows { get; }

    public IExportFlowLoader ExportFlows { get; }

    public IInvokeFlowLoader InvokeFlows { get; }

    public IDataSourceStore DataSources { get; }

    public static FullModeIngestionHost Create(FullModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlConnectionString);

        var control = options.ControlConnectionString;
        var secrets = options.Secrets ?? new SecretResolver([new EnvSecretProvider()]);

        // The SQL Server provider set is built in; MySQL/PostgreSQL sources plug in via options.SourceProviders.
        var registry = SqlServerSourceProvider.CreateRegistry();
        if (options.SourceProviders is not null)
        {
            registry = registry.Concat(options.SourceProviders);
        }

        var store = new SqlDataSourceStore(control);
        var resolver = new ConnectionResolver(store, secrets, registry.Canonicalizers);
        var factory = new CompositeConnectionFactory(registry.ConnectionFactories);
        var catalogs = new CompositeCatalogReaderFactory(registry.CatalogReaders);
        var catalog = new SqlServerCatalogReader();

        IIngestionRunLog runLog = options.EnableRunLog ? new SqlIngestionRunLog(control) : NullIngestionRunLog.Instance;
        IAssertionRunner assertions = options.EnableAssertions
            ? new AssertionRunner(new SqlAssertionDefinitionStore(control))
            : NullAssertionRunner.Instance;
        ISurrogateKeyExecutor surrogateKeys = options.EnableSurrogateKeys
            ? new SurrogateKeyExecutor(resolver, catalog)
            : NullSurrogateKeyExecutor.Instance;

        // The invoke subsystem triggers named external resources only (Azure Data Factory pipelines, Azure
        // Automation runbooks); host code/script execution was removed as an injection risk. The concrete adf/aut
        // executors are drop-in IInvokeExecutor registrations behind the dispatcher, deferred until their Azure
        // SDK packages are referenced; until then the dispatcher reports a set invoke as a clear, logged "no
        // executor registered" failure. The hook runner honors Pre/PostInvokeAlias on the data-flow runners; with
        // EnableInvoke off, those hooks get the Null runner (a set alias errors) while the standalone invoke
        // entry points still work.
        var invokeFlows = new SqlInvokeFlowLoader(control);
        var dispatcher = new InvokeDispatcher(options.InvokeExecutors);
        var invokeRunner = new InvokeFlowRunner(dispatcher, runLog);
        IInvokeRunner hookRunner = options.EnableInvoke
            ? new DefaultInvokeRunner(invokeFlows, dispatcher)
            : NullInvokeRunner.Instance;

        // The pre-ingestion transform's type inference profiles the (SQL Server) target the runner just loaded;
        // the runner hands it an already-resolved connection string, so the env-backed resolver is a pass-through.
        var inference = new Core.Engine.InferenceService(
            new SqlServerSchemaProvider(),
            new SqlServerColumnProfiler(),
            new Core.Engine.TypeInferencer(),
            new SqlServerInferenceValidator(),
            new SqlServerLocaleProvider(),
            new SecretResolver([new EnvSecretProvider()]));

        var runner = new IngestionFlowRunner(
            resolver, factory, catalogs, desiredIndexes: null, runLog: runLog, assertions: assertions,
            surrogateKeys: surrogateKeys, invoke: hookRunner,
            sourceDialects: registry.Dialects, sourceTypeMappers: registry.TypeMappers,
            inference: inference);
        var flows = new SqlIngestionFlowLoader(control);
        var storedProcedureRunner = new StoredProcedureFlowRunner(resolver, runLog, hookRunner);
        var storedProcedureFlows = new SqlStoredProcedureFlowLoader(control);
        var exportRunner = new ExportFlowRunner(resolver, destinations: null, runLog, hookRunner);
        var exportFlows = new SqlExportFlowLoader(control);
        return new FullModeIngestionHost(runner, flows, store, storedProcedureRunner, storedProcedureFlows, exportRunner, exportFlows, invokeRunner, invokeFlows);
    }

    public async Task<IngestionRunResult> RunFlowByIdAsync(int flowId, CancellationToken ct = default)
    {
        var flow = await Flows.LoadByIdAsync(flowId, ct).ConfigureAwait(false);
        return await Runner.RunAsync(flow, ct: ct).ConfigureAwait(false);
    }

    public async Task<IngestionRunResult> RunFlowByAliasAsync(string sysAlias, CancellationToken ct = default)
    {
        var flow = await Flows.LoadByAliasAsync(sysAlias, ct).ConfigureAwait(false);
        return await Runner.RunAsync(flow, ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IngestionRunResult>> RunBatchAsync(string batch, CancellationToken ct = default)
    {
        var flows = await Flows.LoadBatchAsync(batch, ct).ConfigureAwait(false);
        var results = new List<IngestionRunResult>(flows.Count);
        foreach (var flow in flows)
        {
            var result = await Runner.RunAsync(flow, ct: ct).ConfigureAwait(false);
            results.Add(result);

            // A failed flow stops the batch unless it opts into resume (legacy OnErrorResume).
            if (!result.Success && !flow.OnErrorResume)
            {
                break;
            }
        }

        return results;
    }

    public async Task<StoredProcedureRunResult> RunStoredProcedureByIdAsync(int flowId, CancellationToken ct = default)
    {
        var flow = await StoredProcedureFlows.LoadByIdAsync(flowId, ct).ConfigureAwait(false);
        return await _storedProcedureRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredProcedureRunResult>> RunStoredProcedureBatchAsync(string batch, CancellationToken ct = default)
    {
        var flows = await StoredProcedureFlows.LoadBatchAsync(batch, ct).ConfigureAwait(false);
        var results = new List<StoredProcedureRunResult>(flows.Count);
        foreach (var flow in flows)
        {
            var result = await _storedProcedureRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
            results.Add(result);

            if (!result.Success && !flow.OnErrorResume)
            {
                break;
            }
        }

        return results;
    }

    public async Task<ExportRunResult> RunExportByIdAsync(int flowId, CancellationToken ct = default)
    {
        var flow = await ExportFlows.LoadByIdAsync(flowId, ct).ConfigureAwait(false);
        return await _exportRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportRunResult>> RunExportBatchAsync(string batch, CancellationToken ct = default)
    {
        var flows = await ExportFlows.LoadBatchAsync(batch, ct).ConfigureAwait(false);
        var results = new List<ExportRunResult>(flows.Count);
        foreach (var flow in flows)
        {
            var result = await _exportRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
            results.Add(result);

            if (!result.Success && !flow.OnErrorResume)
            {
                break;
            }
        }

        return results;
    }

    public async Task<InvokeResult> RunInvokeByIdAsync(int flowId, CancellationToken ct = default)
    {
        var flow = await InvokeFlows.LoadByIdAsync(flowId, ct).ConfigureAwait(false);
        return await _invokeRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
    }

    public async Task<InvokeResult> RunInvokeByAliasAsync(string invokeAlias, CancellationToken ct = default)
    {
        var flow = await InvokeFlows.LoadByAliasAsync(invokeAlias, ct).ConfigureAwait(false);
        return await _invokeRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InvokeResult>> RunInvokeBatchAsync(string batch, CancellationToken ct = default)
    {
        var flows = await InvokeFlows.LoadBatchAsync(batch, ct).ConfigureAwait(false);
        var results = new List<InvokeResult>(flows.Count);
        foreach (var flow in flows)
        {
            var result = await _invokeRunner.RunAsync(flow, ct: ct).ConfigureAwait(false);
            results.Add(result);

            // A failed invoke stops the batch unless it opts into resume (legacy OnErrorResume).
            if (!result.Success && !flow.OnErrorResume)
            {
                break;
            }
        }

        return results;
    }
}
