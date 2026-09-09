using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.SqlServer.Schema;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>The outcome of one ingestion run.</summary>
public sealed record IngestionRunResult
{
    public required Guid RunId { get; init; }

    public required bool Success { get; init; }

    /// <summary>Rows bulk-copied from the source into staging.</summary>
    public long RowsStaged { get; init; }

    /// <summary>The two-part name of the flow's canonical staging table (in the raw schema).</summary>
    public required string StagingTable { get; init; }

    /// <summary>True when the staging table was left in place (always on failure; on success when the flow
    /// opted to keep it).</summary>
    public bool StagingRetained { get; init; }

    /// <summary>The incremental WHERE fragment appended to the source read after <c>WHERE 1=1</c> (begins
    /// with <c>" AND "</c>, or empty for a full read). Surfaced for observability and tests.</summary>
    public string SourceWhere { get; init; } = string.Empty;

    /// <summary>True when the run took the full-load / insert-all apply path (empty or absent target, or a
    /// keyless flow) rather than the keyed upsert.</summary>
    public bool RunFullLoad { get; init; }

    /// <summary>The outcome of applying the declared (trgDesiredIndex) indexes, empty when none were declared
    /// or the target was not created this run.</summary>
    public IReadOnlyList<IndexAction> IndexActions { get; init; } = [];

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    /// <summary>Wall time in seconds, to milliseconds (the legacy run log keeps its whole-second column).</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Rows the keyed upsert (or insert-all) inserted into the target.</summary>
    public long RowsInserted { get; init; }

    /// <summary>Rows the keyed upsert updated in the target.</summary>
    public long RowsUpdated { get; init; }

    public long RowsDeleted { get; init; }

    /// <summary>Rows per second over the run (0 for a sub-second run).</summary>
    public decimal FlowRate { get; init; }

    /// <summary>Data-quality assertion outcomes (empty in without-database mode or when none are declared).</summary>
    public IReadOnlyList<AssertionResult> Assertions { get; init; } = [];

    /// <summary>Surrogate-key generation outcomes (empty in without-database mode or when none are declared).</summary>
    public IReadOnlyList<SurrogateKeyResult> SurrogateKeys { get; init; } = [];

    /// <summary>Every SQL statement the run generated, in execution order, populated on success AND failure
    /// (the trace captured up to the failure point). This is the run's debugging surface.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    /// <summary>The typed transformation view refreshed over the target as the run's post-process, with its
    /// resolved column projection; null when the flow does not generate one (the native SQL-to-SQL default).</summary>
    public TransformViewResult? TransformView { get; init; }

    /// <summary>The incremental read scope this run computed and applied (mode, source WHERE filter, and the
    /// resolved watermark with its probed object); null on the failure path, where it may not have resolved.</summary>
    public IncrementalSummary? Incremental { get; init; }

    /// <summary>The failure message (already redacted of any secret), or null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Runs one relational (SQL to SQL Server) ingestion flow end to end, the V3 execution of a flw.Ingestion row:
/// resolve the source and target connections through the registry, introspect and shape the source columns,
/// rebuild the flow's canonical staging table, stream the source into it with SqlBulkCopy, evolve the target
/// schema, then apply staging to the target with the two-step keyed upsert (or an insert-all when there is no
/// key). The staging table is canonical per flow, not per execution: it lives in the raw schema (which by
/// itself marks it as staging, so the name carries no stg prefix) and is named after the target it feeds plus
/// the flow id ([raw].[&lt;targetSchema&gt;_&lt;targetTable&gt;_&lt;flowId&gt;]), so it is traceable at a glance and every run
/// rebuilds the same object instead of accumulating per-execution copies.
/// It is dropped on success unless the flow opts to keep it; a failed run always keeps it for debugging, and
/// the next run's rebuild resets it. There is a single execution path: file flows use FlowRunner, relational
/// flows use this runner, and both share the schema-evolution and upsert machinery.
/// </summary>
public sealed class IngestionFlowRunner
{
    /// <summary>The schema hosting every flow's canonical staging and match-key work tables. The schema itself
    /// marks its tables as staging, so the staging name carries no stg prefix. The name lives in
    /// <see cref="StagingConventions"/> so everything that must recognize an engine work table (the
    /// source-control scripter skipping them, for one) reads the same value.</summary>
    public const string StagingSchemaName = StagingConventions.SchemaName;

    // SQL Server's identifier length cap (sysname), which the composed work-table name must respect.
    private const int MaxIdentifierLength = 128;

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IConnectionResolver _resolver;
    private readonly IConnectionFactory _factory;
    private readonly ICatalogReaderFactory _catalogs;
    private readonly IReadOnlyList<ISourceSqlDialect> _dialects;
    private readonly IReadOnlyList<ISourceTypeMapper> _typeMappers;
    private readonly IDesiredIndexManager _desiredIndexes;
    private readonly IIngestionRunLog _runLog;
    private readonly IAssertionRunner _assertions;
    private readonly ISurrogateKeyExecutor _surrogateKeys;
    private readonly IInvokeRunner _invoke;
    private readonly IInferenceService? _inference;

    public IngestionFlowRunner(
        IConnectionResolver resolver,
        IConnectionFactory factory,
        ICatalogReaderFactory catalogs,
        IDesiredIndexManager? desiredIndexes = null,
        IIngestionRunLog? runLog = null,
        IAssertionRunner? assertions = null,
        ISurrogateKeyExecutor? surrogateKeys = null,
        IInvokeRunner? invoke = null,
        IReadOnlyList<ISourceSqlDialect>? sourceDialects = null,
        IReadOnlyList<ISourceTypeMapper>? sourceTypeMappers = null,
        IInferenceService? inference = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(catalogs);

        _resolver = resolver;
        _factory = factory;
        _catalogs = catalogs;

        // The SQL Server dialect/mapper are the default set; provider registries append MySQL/PostgreSQL.
        _dialects = sourceDialects is { Count: > 0 } ? sourceDialects : [new SqlServerSourceDialect()];
        _typeMappers = sourceTypeMappers is { Count: > 0 } ? sourceTypeMappers : [new SqlServerSourceTypeMapper()];
        _desiredIndexes = desiredIndexes ?? new SqlServerDesiredIndexManager();

        // Without-database (YAML) mode logs nothing, asserts nothing, generates no surrogate keys, and has no
        // invoke registry; with-database mode injects the SQL-backed run log, assertion runner, surrogate-key
        // executor, and invoke runner. With no invoke runner wired, a set Pre/PostInvokeAlias is surfaced as a
        // clear error by NullInvokeRunner rather than silently skipped.
        _runLog = runLog ?? NullIngestionRunLog.Instance;
        _assertions = assertions ?? NullAssertionRunner.Instance;
        _surrogateKeys = surrogateKeys ?? NullSurrogateKeyExecutor.Instance;
        _invoke = invoke ?? NullInvokeRunner.Instance;

        // Without an inference service wired, authored (declared) transforms still project into the view; a flow
        // that asks for type INFERENCE is surfaced as a clear error at the point of use rather than silently
        // skipped (same contract as the invoke runner above).
        _inference = inference;
    }

    public async Task<IngestionRunResult> RunAsync(IngestionFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var runToken = runId.ToString("N", CultureInfo.InvariantCulture)[..8];
        var staging = new RelationalObject
        {
            Database = flow.Target.Table.Database,
            Schema = StagingSchemaName,
            Name = CanonicalWorkTableName(prefix: null, flow),
        };
        var applyOptions = new SchemaApplyOptions();

        // An assertions-only run (RunParameters.AssertionsOnly) evaluates the flow's declared assertions against
        // the CURRENT target and does nothing else: no source read, no staging rebuild, no load. It branches off
        // before the source connection resolves, so a broken source can never block an on-demand quality check.
        if (options.Parameters.AssertionsOnly)
        {
            return await RunAssertionsOnlyAsync(flow, options, runId, startUtc, staging, ct).ConfigureAwait(false);
        }

        var resolvedSource = await _resolver.ResolveAsync(flow.Source.ConnectionReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false);
        var resolvedTarget = await _resolver.ResolveAsync(flow.Target.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);
        var targetConnectionString = resolvedTarget.CanonicalString;

        // Per-source provider pieces: the catalog reader, SQL dialect, and type mapper for the source's kind.
        // The target is always SQL Server; its reader drives schema evolution and the target-side watermark
        // probe, both constructed per run (they are cheap and stateless).
        var sourceCatalog = _catalogs.ReaderFor(resolvedSource);
        var sourceDialect = _dialects.FirstOrDefault(d => d.CanHandle(resolvedSource.Kind))
            ?? throw new SqlFlowException(
                $"No source SQL dialect is registered for data source kind '{resolvedSource.Kind}'. Register the matching provider (for example from SqlFlow.Providers).");
        var sourceTypeMapper = _typeMappers.FirstOrDefault(m => m.CanHandle(resolvedSource.Kind))
            ?? throw new SqlFlowException(
                $"No source type mapper is registered for data source kind '{resolvedSource.Kind}'. Register the matching provider (for example from SqlFlow.Providers).");
        var targetCatalog = _catalogs.ReaderFor(resolvedTarget);
        var schemaSync = new SchemaSyncService(targetCatalog);
        var incremental = new IncrementalWindowResolver(targetCatalog, _factory);

        // The run's ordered SQL trace: every statement the engine generates is captured here, on success and
        // (especially) on failure, because the generated SQL is the debugging surface of a metadata-driven run.
        // Each captured statement is also emitted to the canonical run log at Trace level, so one call site
        // feeds both the trace.sql artifact and the timeline.
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;
        void Info(string step, string message) => events.Log(RunLogLevel.Info, step, message);
        void Dbg(string step, string message) => events.Log(RunLogLevel.Debug, step, message);
        var trace = new List<SqlTraceEntry>();
        void Trace(string step, string? sql)
        {
            if (!string.IsNullOrWhiteSpace(sql))
            {
                var entry = new SqlTraceEntry { Sequence = trace.Count + 1, Step = step, Sql = sql };
                trace.Add(entry);
                events.Log(RunLogLevel.Trace, step, sql);
                statements.Report(entry);
            }
        }

        // Attribute a run failure to the exact statement that raised it, so the trace records not just what the run
        // generated but which one broke. ApplyLoadAsync raises a LoadStatementException carrying the offending
        // statement (the load batch traces every statement up front, so "the last one" would be imprecise); every
        // other step is trace-then-execute, so the most recent entry is the one that was running.
        void MarkFailure(Exception failure)
        {
            if (trace.Count == 0)
            {
                return;
            }

            var index = failure is LoadStatementException load
                ? trace.FindLastIndex(e => string.Equals(e.Sql, load.Statement.Sql, StringComparison.Ordinal))
                : trace.Count - 1;
            if (index < 0)
            {
                index = trace.Count - 1;
            }

            trace[index] = trace[index] with { Error = failure.Message };
            statements.ReportFailure(trace[index].Sequence, failure.Message);
        }

        try
        {
            // Fail fast on flags that are accepted by the loaders but not yet implemented by the engine, so a
            // flow can never silently believe it got temporal history or an unknown-member row. These are guarded
            // here (the universal path for YAML, control-DB, and legacy sources) rather than silently ignored.
            EnsureSupportedFeatures(flow);

            // The consolidation-gated landing truncate (step 8c) needs a watermark to compare the two sides and a
            // SQL Server source to issue the T-SQL truncate on. Validate both here, before any data work, so a
            // misconfigured flow fails immediately rather than after a committed load.
            if (flow.Load.TruncateSourceWhenConsolidated)
            {
                if (ConsolidationWatermarkColumn(flow) is null)
                {
                    throw new SqlFlowException(
                        "load.truncateSourceWhenConsolidated requires an incremental watermark (incremental.columns or " +
                        "incremental.dateColumn): the landing table is truncated only once the target's MAX(watermark) has " +
                        "caught up to it, so a watermark column is mandatory.");
                }

                if (resolvedSource.Kind != DataSourceKind.MSSQL)
                {
                    throw new SqlFlowException(
                        $"load.truncateSourceWhenConsolidated is supported only for SQL Server sources (the [pre] landing " +
                        $"database), not '{resolvedSource.Kind}': the landing truncate is issued as T-SQL on the source connection.");
                }
            }

            Info("run.start",
                $"ingestion '{flow.SysAlias ?? flow.Target.Table.Name}' (flow {flow.FlowId}, run {runToken}): " +
                $"{flow.Source.Table.QualifiedName} -> {flow.Target.Table.QualifiedName}, staging {SchemaQualified(staging)}");
            // 0. PreInvokeAlias: run the named flw.Invoke flow before any data work (a prerequisite hook). The
            //    invoke runner resolves the alias and dispatches it; a failure the invoke does not tolerate is
            //    raised here and fails the flow. Without an invoke runner wired (without-database mode), a set
            //    alias is surfaced as a clear error by NullInvokeRunner.
            if (!string.IsNullOrWhiteSpace(flow.Process.PreInvokeAlias))
            {
                Info("invoke.pre", $"running pre-invoke '{flow.Process.PreInvokeAlias.Trim()}'");
                await _invoke.RunByAliasAsync(flow.Process.PreInvokeAlias!.Trim(), ct).ConfigureAwait(false);
            }

            // 1. Introspect the source object and shape its columns: drop ignored columns and clear the
            //    source identity/primary-key facts so staging and target do not inherit them.
            IReadOnlyList<SqlColumn> sourceColumns;
            await using (var sourceConnection = await _factory.OpenAsync(resolvedSource, ct).ConfigureAwait(false))
            {
                var introspected = await sourceCatalog.IntrospectObjectAsync(sourceConnection, ToName(flow.Source.Table), ct).ConfigureAwait(false)
                    ?? throw new SqlFlowException($"Source object {flow.Source.Table.QualifiedName} was not found.");

                sourceColumns = CatalogSchemaAdapter.ToColumns(introspected, sourceTypeMapper)
                    .Where(c => !flow.Source.IgnoreColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(c => c with { IsIdentity = false, IsPrimaryKey = false })
                    .ToList();
            }

            if (sourceColumns.Count == 0)
            {
                throw new SqlFlowException($"Source object {flow.Source.Table.QualifiedName} exposes no columns to ingest.");
            }

            // 2. Build the desired target schema (data + system + hash + identity) and the bulk-copy name map.
            var targetSchema = new IngestionSchemaBuilder(new DefaultColumnNameCleaner()).Build(sourceColumns, flow, forStaging: false);
            var nameMap = targetSchema.SourceToTargetNames;
            var byName = sourceColumns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var bulkColumns = sourceColumns
                .Where(c => nameMap.ContainsKey(c.Name))
                .Select(c => (Source: c.Name, Target: nameMap[c.Name]))
                .ToList();
            var dataColumnNames = bulkColumns.Select(c => c.Target).ToList();
            var stagingColumns = bulkColumns.Select(c => byName[c.Source] with { Name = c.Target }).ToList();

            Info("source.introspect", $"{sourceColumns.Count} source column(s), {bulkColumns.Count} bulk-copied"
                + (flow.Source.IgnoreColumns.Count > 0 ? $", {flow.Source.IgnoreColumns.Count} ignored" : string.Empty));
            Dbg("source.introspect", "column map: " + string.Join(", ", bulkColumns.Select(c =>
                string.Equals(c.Source, c.Target, StringComparison.Ordinal) ? c.Source : $"{c.Source} -> {c.Target}")));

            // 2b. PreProcessOnTarget: raw T-SQL on the target, before any new data is staged or loaded (so it
            //     sees the old/absent target). On a first-ever run the target does not exist yet, so a pre-hook
            //     that touches the target must guard itself with IF OBJECT_ID(...) IS NOT NULL (legacy contract).
            if (TargetProcessHooks.ShouldRun(flow.Process.PreProcessOnTarget))
            {
                Info("target.preprocess", "running the pre-process hook on the target");
                Trace("target.preprocess", flow.Process.PreProcessOnTarget!.Trim());
                await TargetProcessHooks.RunAsync(targetConnectionString, flow.Process.PreProcessOnTarget!.Trim(), ct).ConfigureAwait(false);
            }

            // 2c. Take this flow's work-table lease and hold it for the rest of the run. Everything from the
            //     staging rebuild below to the staging drop at the end operates on tables named for the FLOW, not
            //     for this run, so a second execution of the same flow would drop and recreate the very table this
            //     one is filling (and load whatever rows it found there). The run queue serializes runs of one
            //     pipeline, but it is not the only way a flow executes, so the guarantee is taken here, against the
            //     target database that owns the tables. Nothing has been read or written yet, so a run that cannot
            //     take the lease fails clean.
            await using var workTables = await WorkTableLease.AcquireAsync(
                targetConnectionString, staging, flow.SysAlias ?? flow.Target.Table.Name, WorkTableLease.LockWaitMs, ct)
                .ConfigureAwait(false);
            if (workTables.Waited)
            {
                Info("work.lease", $"work tables {workTables.WorkTable} leased after waiting {workTables.WaitedMs}ms "
                    + "for another execution of this flow to finish");
            }
            else
            {
                Dbg("work.lease", $"work tables {workTables.WorkTable} leased");
            }

            // 3. Rebuild the flow's canonical staging table (data columns only). The raw schema is ensured
            //    first (it also hosts the match-key table below), then the previous incarnation is dropped so
            //    the create always yields a fresh table carrying THIS run's exact source shape: a kept or
            //    failed prior table never leaks stale rows or a stale schema into the run, and a flow never
            //    accumulates more than this one staging object.
            var ensureSchemaSql = $"IF SCHEMA_ID(N'{StagingSchemaName}') IS NULL EXEC(N'CREATE SCHEMA [{StagingSchemaName}]');";
            Trace("staging.schema", ensureSchemaSql);
            await ExecuteAsync(targetConnectionString, ensureSchemaSql, ct).ConfigureAwait(false);
            var resetSql = $"DROP TABLE IF EXISTS {SchemaQualified(staging)};";
            Trace("staging.reset", resetSql);
            await ExecuteAsync(targetConnectionString, resetSql, ct).ConfigureAwait(false);
            var stagingOutcome = await schemaSync.EvolveAsync(targetConnectionString, staging, stagingColumns, NoKeys, allowTableRewrite: false, applyOptions, ct).ConfigureAwait(false);
            Info("staging.create", $"staging table {SchemaQualified(staging)} created");
            foreach (var statement in stagingOutcome.AppliedStatements)
            {
                Trace("staging.create", statement.Text);
            }

            // 3.5 / 4. Fill staging. An InitLoad backfill chunks the source by date/key (ignoring the
            //          incremental watermark, exactly as legacy); otherwise the incremental window bounds a
            //          single read. Both paths feed the SAME staging table, so steps 5-8 are unchanged.
            IncrementalWindow window;
            string? sourceSelect;
            long rowsStaged;
            if (flow.InitLoad.Enabled)
            {
                // Per-run substitution: a trigger-time backfill window re-windows the chunk plan for THIS run,
                // so one InitLoad definition serves any historical slice without a YAML edit.
                var initFlow = flow;
                if (options.Parameters.BackfillFrom is { } chunkFrom)
                {
                    initFlow = flow with
                    {
                        InitLoad = flow.InitLoad with
                        {
                            FromDate = DateOnly.FromDateTime(chunkFrom),
                            ToDate = options.Parameters.BackfillTo is { } chunkTo
                                ? DateOnly.FromDateTime(chunkTo)
                                : flow.InitLoad.ToDate,
                        },
                    };
                    Info("source.initload", $"init-load window overridden by run parameters: {options.Parameters.Describe()}");
                }

                window = new IncrementalWindow { SourceWhere = string.Empty, RunFullLoad = true };
                var segments = InitLoadPlanner.Plan(initFlow, initFlow.Source.Table, bulkColumns.Select(c => c.Source).ToList(), sourceDialect);
                sourceSelect = segments.Count > 0 ? segments[0].Sql : null;
                Info("source.initload", $"init-load backfill: {segments.Count} segment(s), {Math.Max(1, flow.Load.Threads ?? 1)} concurrent");
                foreach (var segment in segments)
                {
                    Trace("source.select.segment", segment.Sql);
                }

                rowsStaged = await BulkCopyInitLoadAsync(resolvedSource, targetConnectionString, staging, bulkColumns, segments, flow.Load, ct).ConfigureAwait(false);
            }
            else
            {
                window = await incremental.ResolveAsync(flow, resolvedSource, targetConnectionString, sourceColumns, sourceDialect, options.Parameters, options.WatermarkSourceTable, ct).ConfigureAwait(false);
                if (!options.Parameters.IsDefault)
                {
                    Info("incremental.window", $"run parameters applied: {options.Parameters.Describe()}");
                }

                if (window.WatermarkSource is { } source && source.StartsWith("downstream ", StringComparison.Ordinal))
                {
                    Info("incremental.window", $"watermark anchored to the downstream table: {source}");
                }

                Info("incremental.window", window.RunFullLoad
                    ? "full load (no usable watermark)"
                    : window.SourceWhere.Length > 0 ? $"incremental read: WHERE 1=1{window.SourceWhere}" : "full read (no incremental bound)");
                Trace("incremental.max-probe", window.TargetMaxProbeSql);
                Trace("incremental.min-probe", window.SourceMinProbeSql);
                sourceSelect = BuildSourceSelect(flow.Source.Table, bulkColumns, window.SourceWhere, sourceDialect);
                Trace("source.select", sourceSelect);
                rowsStaged = await BulkCopyAsync(resolvedSource, sourceSelect, targetConnectionString, staging, bulkColumns, flow.Load, ct).ConfigureAwait(false);
            }

            Info("stage.copy", $"{rowsStaged} row(s) staged");

            // 4.5. Staging stays a HEAP for the plain set-based apply (one hash join reads it once; index
            //      maintenance would cost more than it buys). Only dataset processing indexes it: the ordered
            //      per-dataset loop dedups and filters staging by the dataset column once per distinct dataset,
            //      so it gets the legacy engine's NCI_DataSets nonclustered index after the bulk copy.
            if (flow.Load.DataSetColumn is { } dataSetColumn && rowsStaged > 0)
            {
                var mappedDataSet = MapName(nameMap, dataSetColumn);
                var stagingIndexSql =
                    $"CREATE NONCLUSTERED INDEX [NCI_DataSets] ON {SchemaQualified(staging)} ([{Escape(mappedDataSet)}] ASC);";
                Trace("staging.index", stagingIndexSql);
                await ExecuteAsync(targetConnectionString, stagingIndexSql, ct).ConfigureAwait(false);
                Info("staging.index", $"NCI_DataSets on staging ([{mappedDataSet}])");
            }

            // 5. Evolve the target to the desired schema when schema sync is enabled. Capture whether the
            //    target was created this run, so the create-time index steps fire exactly once.
            var targetCreated = false;
            string? createCmd = null;
            if (flow.SchemaSync.Sync)
            {
                var targetOutcome = await schemaSync.EvolveAsync(
                    targetConnectionString,
                    flow.Target.Table,
                    targetSchema.Columns,
                    KeySet(EffectiveKeyColumns(flow)),
                    flow.SchemaSync.AllowTableRewrite,
                    applyOptions,
                    ct).ConfigureAwait(false);
                targetCreated = targetOutcome.Plan.CreateTable;
                createCmd = targetOutcome.AppliedStatements.FirstOrDefault(s => s.IsCreateTable)?.Text;
                Info("target.evolve", targetCreated
                    ? $"target {flow.Target.Table.QualifiedName} created"
                    : targetOutcome.AppliedStatements.Count > 0
                        ? $"target schema evolved: {targetOutcome.AppliedStatements.Count} change(s) applied"
                        : "target schema up to date");
                foreach (var statement in targetOutcome.AppliedStatements)
                {
                    Trace("target.evolve", statement.Text);
                }
            }

            // 5.5. On the run that created the target, add the canonical indexes on the still-empty table
            //      (key/date/dataset/UpdatedDate_DW, optional clustered columnstore), using mapped target names.
            if (targetCreated)
            {
                var canonical = CanonicalIndexPlanner.Plan(
                    flow.Target.Table,
                    EffectiveKeyColumns(flow).Select(k => MapName(nameMap, k)).ToList(),
                    MapNameOrNull(nameMap, flow.Incremental.DateColumn),
                    MapNameOrNull(nameMap, flow.Source.DataSetColumn),
                    hasUpdatedDateColumn: HasColumn(targetSchema.Columns, "UpdatedDate_DW"),
                    flow.Target.ColumnStoreIndex,
                    hasIdentityPrimaryKey: targetSchema.Columns.Any(c => c.IsPrimaryKey),
                    scd2Enabled: flow.Versioning.Scd2.Enabled,
                    reloadColumn: MapNameOrNull(nameMap, flow.Load.ReloadColumn));
                Info("target.index.canonical", $"{canonical.Count} canonical index(es) on the new target");
                foreach (var statement in canonical)
                {
                    Trace("target.index.canonical", statement);
                    await ExecuteAsync(targetConnectionString, statement, ct).ConfigureAwait(false);
                }
            }

            // 5.6. SCD2 key index: the business key is unique only among CURRENT rows, so the key index is a
            //      filtered unique index. This runs whenever SCD2 is on (any table state), so enabling SCD2 on
            //      a pre-existing table migrates its plain unique key index to the filtered form before the
            //      load inserts a second version for a key. It is guarded, so it is a no-op once in place.
            if (flow.Versioning.Scd2.Enabled)
            {
                var scd2Indexes = CanonicalIndexPlanner.Scd2KeyIndexStatements(
                    flow.Target.Table,
                    EffectiveKeyColumns(flow).Select(k => MapName(nameMap, k)).ToList(),
                    flow.Versioning.Scd2.CurrentFlagColumn);
                foreach (var statement in scd2Indexes)
                {
                    Trace("target.index.scd2", statement);
                    await ExecuteAsync(targetConnectionString, statement, ct).ConfigureAwait(false);
                }
            }

            // 5.7. System-versioned temporal history. This runs AFTER schema evolution and the create-time
            //      indexes, for two reasons: SQL Server refuses to version a table with no PRIMARY KEY (which
            //      the create step is what supplies), and adding the SYSTEM_TIME period last means the period
            //      columns are never in the desired schema the evolution planner diffs. Ordinary evolution
            //      keeps working afterwards: ADD, ALTER and DROP COLUMN are all supported while versioning is
            //      on and SQL Server propagates each to the history table, so the engine never has to take
            //      versioning off to fit a column change through.
            if (flow.Versioning.Temporal.Enabled)
            {
                var temporalOutcome = await schemaSync.ApplyTemporalAsync(
                    targetConnectionString,
                    flow.Target.Table,
                    flow.Versioning.Temporal,
                    targetSchema.Columns,
                    applyOptions,
                    ct).ConfigureAwait(false);

                Info("target.temporal", temporalOutcome.Plan.Action switch
                {
                    TemporalAction.AlreadyCurrent =>
                        $"target {flow.Target.Table.QualifiedName} is already system-versioned into {temporalOutcome.Plan.HistoryName}",
                    TemporalAction.AddPeriodAndEnable =>
                        $"system versioning enabled on {flow.Target.Table.QualifiedName}: SYSTEM_TIME period added and history " +
                        $"kept in {temporalOutcome.Plan.HistoryName}",
                    TemporalAction.EnableOnExistingPeriod =>
                        $"system versioning re-enabled on {flow.Target.Table.QualifiedName} over its existing SYSTEM_TIME " +
                        $"period; history in {temporalOutcome.Plan.HistoryName}",
                    _ => $"history retention on {temporalOutcome.Plan.HistoryName} set to " +
                         (flow.Versioning.Temporal.RetentionDays is { } d ? $"{d} day(s)" : "INFINITE"),
                });

                foreach (var statement in temporalOutcome.AppliedStatements)
                {
                    Trace("target.temporal", statement.Text);
                }
            }

            // 6. Optional full-reload truncate.
            if (flow.Target.TruncateBeforeLoad)
            {
                var truncateSql = $"TRUNCATE TABLE {SchemaQualified(flow.Target.Table)};";
                Info("target.truncate", $"target {flow.Target.Table.QualifiedName} truncated before load");
                Trace("target.truncate", truncateSql);
                await ExecuteAsync(targetConnectionString, truncateSql, ct).ConfigureAwait(false);
            }

            // 7. Apply staging to the target: the keyed two-step upsert (anti-join safe), or a blind
            //    insert-all for a keyless flow. The default runs in one transaction; the batched
            //    (lock-escalation-avoiding) apply commits per key window. Counts attribute by statement kind.
            // The target's ACTUAL column types, read after the evolve so they are this run's truth. Change
            // detection needs them: a target the flow did not create (schema sync off) stores columns under
            // types of its own, and hashing a staged row against a target row without accounting for that
            // compares two renderings of the same value and rewrites every matched row on every run.
            var targetColumnTypes = await ReadColumnTypesAsync(targetCatalog, targetConnectionString, flow.Target.Table, ct).ConfigureAwait(false);
            var loadStatements = BuildLoadStatements(flow, staging, dataColumnNames, stagingColumns, nameMap, targetColumnTypes, events);
            foreach (var statement in loadStatements)
            {
                Trace(statement.Kind switch
                {
                    LoadKind.Update => "upsert.update",
                    LoadKind.Insert => "upsert.insert",
                    LoadKind.UpsertLoop => "upsert.dataset-loop",
                    LoadKind.Purge => "upsert.purge",
                    _ => "upsert.insert-all",
                }, statement.Sql);
            }

            Dbg("upsert.apply", (string.IsNullOrWhiteSpace(flow.Load.DataSetColumn), flow.Load.BatchUpsertToAvoidLockEscalation) switch
            {
                (false, true) => $"dataset-partitioned loop by [{flow.Load.DataSetColumn}], batched in key windows of {flow.Load.BatchUpsertRowCount} row(s)",
                (false, false) => $"dataset-partitioned loop by [{flow.Load.DataSetColumn}], one transaction",
                (true, true) => $"batched apply: key windows of {flow.Load.BatchUpsertRowCount} row(s), per-window commits (no enclosing transaction)",
                _ => "single-transaction apply",
            });
            // Per-file replace always runs in one transaction so the purge and the insert are atomic (old rows
            // gone and new rows in, or neither); it never uses the per-window-commit batched apply.
            var reloadReplace = !string.IsNullOrWhiteSpace(flow.Load.ReloadColumn);
            var (rowsInserted, rowsUpdated, rowsPurged) = await ApplyLoadAsync(
                targetConnectionString, loadStatements,
                useTransaction: reloadReplace || !flow.Load.BatchUpsertToAvoidLockEscalation, ct).ConfigureAwait(false);
            Info("upsert.apply", reloadReplace
                ? $"per-file replace on [{flow.Load.ReloadColumn}]: {rowsPurged} purged, {rowsInserted} inserted"
                : $"{rowsInserted} inserted, {rowsUpdated} updated");
            var insertCmd = loadStatements.FirstOrDefault(s => s.Kind is not LoadKind.Update and not LoadKind.Purge)?.Sql;
            var updateCmd = loadStatements.FirstOrDefault(s => s.Kind == LoadKind.Update)?.Sql;

            // 7a. PostProcessOnTarget: raw T-SQL on the target, after the load commits, outside the load
            //     transaction (a failure here does not roll back the committed load) and before staging is
            //     dropped (so a failure keeps staging for debugging).
            if (TargetProcessHooks.ShouldRun(flow.Process.PostProcessOnTarget))
            {
                Info("target.postprocess", "running the post-process hook on the target");
                Trace("target.postprocess", flow.Process.PostProcessOnTarget!.Trim());
                await TargetProcessHooks.RunAsync(targetConnectionString, flow.Process.PostProcessOnTarget!.Trim(), ct).ConfigureAwait(false);
            }

            // 7b. Generate surrogate keys for the loaded target and write them back (post-commit, log-only:
            //     a per-spec failure is surfaced on the result, never a rollback of the committed load).
            var surrogateResults = await _surrogateKeys.RunAsync(flow, targetConnectionString, ct).ConfigureAwait(false);
            foreach (var surrogate in surrogateResults)
            {
                Info($"surrogate-key", surrogate.Error is not null
                    ? $"{surrogate.SurrogateTable}: FAILED - {surrogate.Error}"
                    : $"{surrogate.SurrogateTable}: {surrogate.KeysGenerated} key(s) generated, {surrogate.RowsStamped} row(s) stamped{(surrogate.IsRemote ? " (remote)" : string.Empty)}");
                foreach (var statement in surrogate.Statements)
                {
                    Trace($"surrogate-key.{surrogate.SurrogateTable}", statement);
                }
            }

            // 7b2. Key match (deleted-row detection): land the full distinct SOURCE key set in the flow's
            //      canonical key table on the target, then tag or delete target rows whose keys vanished from the
            //      source. Runs after the load and the surrogate keys (legacy order) on EVERY run, including
            //      incremental ones: the key fetch never uses the incremental window, so a row deleted outside
            //      the window is still detected. The threshold guard skips a suspicious mass delete loudly.
            // Rows removed by the per-file replace purge count as deletions (match keys and reloadColumn are
            // mutually exclusive by validation, so these never double-count).
            long rowsDeleted = rowsPurged;
            RelationalObject? matchKeyTable = null;
            if (flow.Load.MatchKeysInSourceAndTarget)
            {
                matchKeyTable = new RelationalObject
                {
                    Database = flow.Target.Table.Database,
                    Schema = StagingSchemaName,
                    Name = CanonicalWorkTableName("mkey", flow),
                };
                rowsDeleted = await RunMatchKeysAsync(
                    flow, resolvedSource, targetConnectionString, matchKeyTable, nameMap, sourceDialect, Info, Dbg, Trace, ct).ConfigureAwait(false);
            }

            // 7.5. Apply the declared (trgDesiredIndex) indexes, only on the run that created the target (the
            //      create-only manager would fail a duplicate CREATE on a later run). A malformed script is
            //      surfaced as a failed action rather than rolling back the committed load (legacy continues).
            IReadOnlyList<IndexAction> indexActions = [];
            if (targetCreated && !string.IsNullOrWhiteSpace(flow.Target.DesiredIndexes))
            {
                try
                {
                    indexActions = await _desiredIndexes.ApplyAsync(targetConnectionString, flow.Target.DesiredIndexes!, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The load already committed; a declared-index failure (a parse error, or the manager's
                    // own connection/SQL failure) is surfaced as a failed action, never a rollback of a good
                    // load. Legacy logs and continues here too.
                    indexActions = [new IndexAction { IndexName = "(desired-index)", Table = flow.Target.Table.Name, Kind = IndexActionKind.Failed, Detail = ex.Message }];
                }
            }

            foreach (var action in indexActions)
            {
                Info("target.index.desired", $"{action.IndexName}: {action.Kind}{(action.Detail is null ? string.Empty : $" ({action.Detail})")}");
                Trace("target.index.desired", action.Sql);
            }

            // 7c. Run data-quality assertions against the loaded target. Legacy parity: log-only and
            //     non-blocking, so Success is unaffected; one assertion failing does not stop the others. Only
            //     the auto-mode assertions run here; manual-mode ones wait for an assertions-only trigger.
            var assertionResults = await _assertions.RunAsync(flow, targetConnectionString, includeManual: false, ct).ConfigureAwait(false);
            foreach (var assertion in assertionResults)
            {
                Info($"assertion", assertion.Error is not null
                    ? $"{assertion.Name}: FAILED - {assertion.Error}"
                    : assertion.Evaluated ? $"{assertion.Name}: result {assertion.Result}" : $"{assertion.Name}: skipped");
                Trace($"assertion.{assertion.Name}", assertion.MaterializedSql);
            }

            // 7d. PostInvokeAlias: run the named flw.Invoke flow after the load commits and before staging is
            //     dropped (a failure keeps staging for debugging). A failure the invoke does not tolerate is
            //     raised; the data is already committed, so the run is reported as failed but the load stands.
            if (!string.IsNullOrWhiteSpace(flow.Process.PostInvokeAlias))
            {
                Info("invoke.post", $"running post-invoke '{flow.Process.PostInvokeAlias.Trim()}'");
                await _invoke.RunByAliasAsync(flow.Process.PostInvokeAlias!.Trim(), ct).ConfigureAwait(false);
            }

            // 8. Drop the canonical staging and key-match tables on success unless the flow asked to keep
            //    them (a failure keeps both for debugging, like staging always has been; the next run's
            //    rebuild resets whatever was kept).
            if (!flow.Load.KeepStagingTable)
            {
                var dropSql = $"DROP TABLE IF EXISTS {SchemaQualified(staging)};";
                Info("staging.drop", $"staging {SchemaQualified(staging)} dropped");
                Trace("staging.drop", dropSql);
                await ExecuteAsync(targetConnectionString, dropSql, ct).ConfigureAwait(false);

                if (matchKeyTable is not null)
                {
                    var dropKeysSql = $"DROP TABLE IF EXISTS {SchemaQualified(matchKeyTable)};";
                    Trace("matchkeys.keytable.drop", dropKeysSql);
                    await ExecuteAsync(targetConnectionString, dropKeysSql, ct).ConfigureAwait(false);
                }
            }
            else
            {
                // The staging table is kept (for inspection or reuse). TruncatePreTableOnCompletion empties it
                // after a successful load so the kept table carries structure without the run's data; without it
                // the kept table retains its rows. A dropped staging (the default branch above) is the stronger
                // cleanup, so the flag only bites when the table is deliberately kept.
                if (flow.Load.TruncatePreTableOnCompletion)
                {
                    var truncateSql = $"TRUNCATE TABLE {SchemaQualified(staging)};";
                    Info("staging.truncate", $"staging {SchemaQualified(staging)} kept and truncated (truncateStagingOnCompletion)");
                    Trace("staging.truncate", truncateSql);
                    await ExecuteAsync(targetConnectionString, truncateSql, ct).ConfigureAwait(false);
                }
                else
                {
                    Info("staging.drop", $"staging {SchemaQualified(staging)} kept (keepStagingTable)");
                }
            }

            // 8b. Pre-ingestion transform view: refresh the typed view over the loaded target (the external-DB
            //     landing contract: the flow's target is the pre/staging table, and the downstream chained flow
            //     reads [schema].[v<Table>] for correctly-typed data). Native SQL-to-SQL flows leave the policy
            //     at its default and generate nothing. A failure here fails the run (a stale view must be loud);
            //     the committed load stands, and CREATE OR ALTER makes the re-run idempotent.
            TransformViewResult? transformView = null;
            if (flow.Transform.GeneratesView)
            {
                transformView = await GenerateTransformViewAsync(flow, targetConnectionString, targetSchema.Columns, ct).ConfigureAwait(false);
                Info("transform.view", $"transformation view [{flow.Target.Table.Schema}].[{transformView.ViewName}] refreshed "
                    + $"({transformView.Columns.Count} column(s), {transformView.Columns.Count(c => c.Converted)} typed)");
                Trace("transform.view", transformView.Ddl);
            }

            // 8c. Consolidation-gated landing truncate (load.truncateSourceWhenConsolidated). Empty the upstream
            //     [pre] landing table that feeds the source view, but ONLY once the target has caught up: compare
            //     MAX(watermark) on both sides and truncate the landing table only when the target's mark is at
            //     least the landing table's. This is the safe alternative to an unconditional truncate: a target
            //     that has not yet consolidated the landed rows keeps them (nothing is lost), while a caught-up
            //     target reclaims the landing table so it stops growing across runs. It runs only on the success
            //     path (a failed run's catch below never reaches here) and after the load has committed, so the
            //     landing rows are already durably in the target before they are discarded.
            if (flow.Load.TruncateSourceWhenConsolidated)
            {
                var wmColumn = ConsolidationWatermarkColumn(flow)!;   // non-null: validated at run start
                var landing = LandingTableOf(flow.Source.Table);
                var landingSql = SchemaQualified(landing);
                var sourceMax = await ReadMaxAsync(resolvedSource.CanonicalString, landingSql, wmColumn, whereClause: null, ct).ConfigureAwait(false);

                if (sourceMax is null)
                {
                    Info("source.truncate", $"landing table {landingSql} is empty; nothing to truncate");
                }
                else
                {
                    // The target probe is scoped by the flow's incrementalClause, exactly like the watermark
                    // window probe: on a shared target (several operators merging into one arc table,
                    // discriminated by a clause like "AND [SourceSystemID] = 31"), an unscoped MAX would return
                    // whichever operator loaded last and fake the catch-up, truncating landing rows this flow
                    // has NOT consolidated. The landing side stays unscoped: the landing table is per-flow, and
                    // the clause may reference view-only (virtual) columns that do not exist on the base table.
                    var targetMax = await ReadMaxAsync(
                        targetConnectionString, SchemaQualified(flow.Target.Table), wmColumn, flow.Source.IncrementalClause, ct).ConfigureAwait(false);
                    if (targetMax is null || CompareWatermarks(targetMax, sourceMax) < 0)
                    {
                        Info("source.truncate",
                            $"landing table {landingSql} retained: target MAX([{wmColumn}]) {Describe(targetMax)} has not caught up to landing MAX {Describe(sourceMax)}");
                    }
                    else
                    {
                        var truncateSql = $"TRUNCATE TABLE {landingSql};";
                        Trace("source.truncate", truncateSql);
                        await ExecuteAsync(resolvedSource.CanonicalString, truncateSql, ct).ConfigureAwait(false);
                        Info("source.truncate",
                            $"landing table {landingSql} truncated: target MAX([{wmColumn}]) {Describe(targetMax)} >= landing MAX {Describe(sourceMax)}");
                    }
                }
            }

            // 9. Record the run. A write failure on a SUCCESSFUL run surfaces (logging is part of the contract
            //    in with-database mode); in without-database mode the no-op log never throws.
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            var flowRate = FlowRateOf(rowsStaged, duration);
            // The wave board shows this line verbatim, so it has to carry the counts: a rate alone reads as "nothing
            // happened" on a fast run, and reads identically whether the flow moved no rows or a million.
            var deletedNote = rowsDeleted > 0 ? $", {rowsDeleted} deleted" : string.Empty;
            Info("run.end",
                $"SUCCESS in {Fmt(duration)}s: {rowsStaged} row(s) staged, {rowsInserted} inserted, "
                + $"{rowsUpdated} updated{deletedNote} ({Fmt((double)flowRate)} rows/s)");
            await _runLog.WriteAsync(
                BuildRunRecord(flow, options, runId, startUtc, endUtc, WholeSeconds(duration), rowsStaged, rowsInserted, rowsUpdated, rowsDeleted, success: true, error: null, sourceSelect, insertCmd, updateCmd, createCmd, flowRate, SqlTrace.Render(trace)),
                ct).ConfigureAwait(false);

            return new IngestionRunResult
            {
                RunId = runId,
                Success = true,
                RowsStaged = rowsStaged,
                StagingTable = SchemaQualified(staging),
                StagingRetained = flow.Load.KeepStagingTable,
                SourceWhere = window.SourceWhere,
                RunFullLoad = window.RunFullLoad,
                IndexActions = indexActions,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                RowsInserted = rowsInserted,
                RowsUpdated = rowsUpdated,
                RowsDeleted = rowsDeleted,
                FlowRate = flowRate,
                Assertions = assertionResults,
                SurrogateKeys = surrogateResults,
                SqlTrace = trace,
                TransformView = transformView,
                Incremental = BuildIncrementalSummary(flow, options, window),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the staging table on failure: it holds the data needed to debug what went wrong. The SQL
            // trace captured so far rides along, with the offending statement marked; the failure case is where it
            // matters most.
            MarkFailure(ex);
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            Info("run.end", $"FAILED after {Fmt(duration)}s: {ex.Message} (staging {SchemaQualified(staging)} kept)");
            try
            {
                await _runLog.WriteAsync(
                    BuildRunRecord(flow, options, runId, startUtc, endUtc, WholeSeconds(duration), 0, 0, 0, 0, success: false, error: ex.Message, selectCmd: null, insertCmd: null, updateCmd: null, createCmd: null, flowRate: 0m, traceLog: SqlTrace.Render(trace)),
                    ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real run error, which is returned below.
            }

            return new IngestionRunResult
            {
                RunId = runId,
                Success = false,
                StagingTable = SchemaQualified(staging),
                StagingRetained = true,
                Error = SecretHygiene.RedactedMessage(ex),
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
            };
        }
    }

    /// <summary>
    /// The assertions-only execution (RunParameters.AssertionsOnly): resolve the target connection, evaluate the
    /// flow's WHOLE assertion list (auto and manual alike; the on-demand run is precisely how a manual assertion
    /// is meant to execute), and record the run. The log-only contract carries over per assertion: a failing or
    /// erroring assertion is recorded on its own result and never fails the run; only an infrastructure failure
    /// (an unresolvable connection, an unreachable target) fails the run itself. The target is never written.
    /// </summary>
    private async Task<IngestionRunResult> RunAssertionsOnlyAsync(
        IngestionFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, RelationalObject staging, CancellationToken ct)
    {
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;
        void Info(string step, string message) => events.Log(RunLogLevel.Info, step, message);
        var trace = new List<SqlTraceEntry>();

        try
        {
            var resolvedTarget = await _resolver.ResolveAsync(flow.Target.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);

            Info("run.start",
                $"assertions-only run '{flow.SysAlias ?? flow.Target.Table.Name}' (flow {flow.FlowId}, run {runId.ToString("N", CultureInfo.InvariantCulture)[..8]}): " +
                $"{flow.Assertions.Count} declared assertion(s) against {flow.Target.Table.QualifiedName}");

            var assertionResults = await _assertions.RunAsync(flow, resolvedTarget.CanonicalString, includeManual: true, ct).ConfigureAwait(false);
            foreach (var assertion in assertionResults)
            {
                Info("assertion", assertion.Error is not null
                    ? $"{assertion.Name}: FAILED - {assertion.Error}"
                    : assertion.Evaluated ? $"{assertion.Name}: result {assertion.Result}" : $"{assertion.Name}: skipped");
                if (!string.IsNullOrWhiteSpace(assertion.MaterializedSql))
                {
                    var entry = new SqlTraceEntry { Sequence = trace.Count + 1, Step = $"assertion.{assertion.Name}", Sql = assertion.MaterializedSql };
                    trace.Add(entry);
                    events.Log(RunLogLevel.Trace, entry.Step, entry.Sql);
                    statements.Report(entry);
                }
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            Info("run.end", $"SUCCESS in {Fmt(duration)}s ({assertionResults.Count} assertion(s) evaluated)");
            await _runLog.WriteAsync(
                BuildRunRecord(flow, options, runId, startUtc, endUtc, WholeSeconds(duration), 0, 0, 0, 0, success: true, error: null, selectCmd: null, insertCmd: null, updateCmd: null, createCmd: null, flowRate: 0m, traceLog: SqlTrace.Render(trace)),
                ct).ConfigureAwait(false);

            return new IngestionRunResult
            {
                RunId = runId,
                Success = true,
                StagingTable = SchemaQualified(staging),
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                Assertions = assertionResults,
                SqlTrace = trace,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            Info("run.end", $"FAILED after {Fmt(duration)}s: {ex.Message}");
            try
            {
                await _runLog.WriteAsync(
                    BuildRunRecord(flow, options, runId, startUtc, endUtc, WholeSeconds(duration), 0, 0, 0, 0, success: false, error: ex.Message, selectCmd: null, insertCmd: null, updateCmd: null, createCmd: null, flowRate: 0m, traceLog: SqlTrace.Render(trace)),
                    ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real run error, which is returned below.
            }

            return new IngestionRunResult
            {
                RunId = runId,
                Success = false,
                StagingTable = SchemaQualified(staging),
                Error = SecretHygiene.RedactedMessage(ex),
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
            };
        }
    }

    // Type families that cannot participate in the CONCAT-based change checksum (the legacy
    // InvalidChecksumDataTypes rule, extended with geography's sibling geometry and the binary family);
    // such a column would raise "Argument data type ... is invalid" at run time.
    private static readonly IReadOnlySet<string> NonChecksumTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "xml", "geography", "geometry", "hierarchyid", "image", "text", "ntext",
        "varbinary", "binary", "rowversion", "timestamp", "sql_variant",
    };

    // SqlFlow-generated columns that do NOT carry the reserved "_DW" suffix. The suffix is the engine's own
    // marker for a system column (IngestionSchemaBuilder sorts every "_DW" column last), so it identifies the
    // provenance, audit, hash, and SCD2 columns by name; FileLineNumber is the sole generated column that
    // predates the convention and lacks the suffix, so it is named explicitly.
    private static readonly IReadOnlySet<string> NonSuffixedSystemColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "FileLineNumber",
    };

    // A column SqlFlow itself materialises (file provenance such as FileName_DW / FileDate_DW / RowNumber_DW,
    // the audit stamps, the surrogate-key hashes HashKey_DW / ConcatKey_DW, and the SCD2 period columns) rather
    // than one carried from the source. Such columns are never source data and must not feed the change
    // checksum, mirroring the legacy engine's IgnoreChecksumColumns list.
    private static bool IsSqlFlowSystemColumn(string columnName)
        => columnName.EndsWith("_DW", StringComparison.OrdinalIgnoreCase)
           || NonSuffixedSystemColumns.Contains(columnName);

    // The key-match pass (legacy MatchKeysInSrcTrg, re-engineered): the comparison runs in SQL on the target
    // (an anti-join against the landed source key set), not in a client-side stream, so it cannot misalign.
    private async Task<long> RunMatchKeysAsync(
        IngestionFlow flow,
        ResolvedConnection resolvedSource,
        string targetConnectionString,
        RelationalObject keyTable,
        IReadOnlyDictionary<string, string> nameMap,
        ISourceSqlDialect sourceDialect,
        Action<string, string> info,
        Action<string, string> dbg,
        Action<string, string?> traceSql,
        CancellationToken ct)
    {
        var policy = flow.MatchKeys;
        var sourceKeys = EffectiveKeyColumns(flow);
        if (sourceKeys.Count == 0)
        {
            throw new SqlFlowException(
                "MatchKeysInSourceAndTarget is on, but the flow has no key columns (set load.keyColumns or matchKeys.keyColumns).");
        }

        if (policy.Action == MatchKeyAction.Tag && !flow.SystemColumns.DeletedDate)
        {
            throw new SqlFlowException(
                "MatchKeys Tag mode soft-deletes by stamping DeletedDate_DW; enable the DeletedDate_DW system column.");
        }

        string? windowDateColumn = null;
        if (policy.IgnoreDeletedRowsAfterMonths is not null)
        {
            var declared = policy.DateColumn ?? flow.Incremental.DateColumn
                ?? throw new SqlFlowException(
                    "matchKeys.ignoreDeletedRowsAfterMonths requires matchKeys.dateColumn (or an incremental dateColumn).");
            windowDateColumn = MapName(nameMap, declared);
        }

        var keyPairs = sourceKeys.Select(k => (Source: k, Target: MapName(nameMap, k))).ToList();
        var targetKeys = keyPairs.Select(p => p.Target).ToList();

        // The flow-canonical key table clones the target key columns' exact types and collations, indexed for
        // the anti-join. Like staging it is rebuilt per run (the raw schema was ensured at staging create), so
        // a prior kept incarnation never collides with the SELECT INTO.
        var keyColumnList = string.Join(", ", targetKeys.Select(k => $"[{Escape(k)}]"));
        var resetKeysSql = $"DROP TABLE IF EXISTS {SchemaQualified(keyTable)};";
        traceSql("matchkeys.keytable.reset", resetKeysSql);
        await ExecuteAsync(targetConnectionString, resetKeysSql, ct).ConfigureAwait(false);
        var createSql =
            $"SELECT TOP (0) {keyColumnList} INTO {SchemaQualified(keyTable)} FROM {SchemaQualified(flow.Target.Table)}; " +
            $"CREATE CLUSTERED INDEX [IX_{Escape(keyTable.Name)}] ON {SchemaQualified(keyTable)} ({keyColumnList});";
        traceSql("matchkeys.keytable", createSql);
        await ExecuteAsync(targetConnectionString, createSql, ct).ConfigureAwait(false);

        // The full distinct source key set, bounded only by the static filter, never the incremental window
        // (a row deleted outside the window must still be detected). An explicit matchKeys.sourceFilter wins;
        // the default is the flow's own source filter, so rows the load never reads are not treated as
        // deleted (the legacy default of no filter deleted them).
        var filter = !string.IsNullOrWhiteSpace(policy.SourceFilter) ? policy.SourceFilter : flow.Source.Filter;
        var staticWhere = string.IsNullOrWhiteSpace(filter) ? string.Empty : " " + filter.Trim();
        var keySelect =
            $"SELECT DISTINCT {string.Join(", ", keyPairs.Select(p => sourceDialect.QuoteIdentifier(p.Source)))} " +
            $"FROM {sourceDialect.QualifyObject(flow.Source.Table)} WHERE 1=1{staticWhere}";
        traceSql("matchkeys.source-keys", keySelect);
        var keysCopied = await BulkCopyAsync(resolvedSource, keySelect, targetConnectionString, keyTable, keyPairs, flow.Load, ct).ConfigureAwait(false);
        dbg("matchkeys", $"{keysCopied} distinct source key(s) landed in {SchemaQualified(keyTable)}");

        var script = MatchKeyGenerator.Generate(flow.Target.Table, keyTable, new MatchKeyScriptOptions
        {
            KeyColumns = targetKeys,
            Action = policy.Action,
            ActionThresholdPercent = policy.ActionThresholdPercent,
            IgnoreDeletedRowsAfterMonths = policy.IgnoreDeletedRowsAfterMonths,
            DateColumn = windowDateColumn,
            TargetFilter = policy.TargetFilter,
            DeletedDateColumn = flow.SystemColumns.DeletedDate ? "DeletedDate_DW" : null,
            RowStatusColumn = flow.SystemColumns.RowStatus ? "RowStatus_DW" : null,
        });
        traceSql("matchkeys.apply", script);

        var counters = await ExecuteMatchKeyScriptAsync(targetConnectionString, script, ct).ConfigureAwait(false);
        var verb = policy.Action == MatchKeyAction.Delete ? "deleted" : "tagged deleted";
        var resurrected = policy.Action == MatchKeyAction.Tag ? $", {counters.ResurrectedRows} resurrected" : string.Empty;
        if (counters.ThresholdBreached)
        {
            var percent = counters.TotalRows > 0 ? counters.CandidateRows * 100.0 / counters.TotalRows : 0;
            info("matchkeys",
                $"WARNING: action SKIPPED - {counters.CandidateRows} of {counters.TotalRows} target row(s) ({percent:0.#}%) " +
                $"would be {verb}, above the {policy.ActionThresholdPercent}% threshold{resurrected}. A mass key " +
                "disappearance is usually a broken source read; raise matchKeys.thresholdPercent to proceed.");
        }
        else
        {
            info("matchkeys", $"{counters.AffectedRows} row(s) {verb} ({counters.CandidateRows} candidate(s) of {counters.TotalRows}){resurrected}");
        }

        return counters.AffectedRows;
    }

    private static async Task<MatchKeyCounters> ExecuteMatchKeyScriptAsync(string connectionString, string script, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(script, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new SqlFlowException("The key-match script returned no counter row.");
        }

        return new MatchKeyCounters
        {
            TotalRows = reader.GetInt64(0),
            CandidateRows = reader.GetInt64(1),
            AffectedRows = reader.GetInt64(2),
            ResurrectedRows = reader.GetInt64(3),
            ThresholdBreached = reader.GetBoolean(4),
        };
    }

    /// <summary>
    /// The pre-ingestion transform post-process for the relational path: profiles the loaded target when
    /// inference is on (authored transforms alone need no profiling), merges the authored transforms over the
    /// inferred columns, and refreshes <c>[schema].[v&lt;Table&gt;]</c> with the resolved projection. The target's
    /// just-applied desired columns are the projection base, so evolved columns are included.
    /// </summary>
    private async Task<TransformViewResult> GenerateTransformViewAsync(
        IngestionFlow flow, string targetConnectionString, IReadOnlyList<SqlColumn> targetColumns, CancellationToken ct)
    {
        IReadOnlyList<InferredColumn> inferred = [];
        if (flow.Transform.Enabled)
        {
            if (_inference is null)
            {
                throw new SqlFlowException(
                    "transform.inferTypes is on, but no inference service is wired into this runner. Register one "
                    + "(the engine host does by default), or declare the transforms explicitly under transform.columns.");
            }

            var report = await _inference.InferAsync(new InferenceRequest
            {
                Connection = targetConnectionString,
                Schema = flow.Target.Table.Schema,
                Table = flow.Target.Table.Name,
                Policy = flow.Transform,
            }, ct).ConfigureAwait(false);

            inferred = report.Columns.Select(c => new InferredColumn
            {
                ColumnName = c.ColumnName,
                DataType = c.DataType,
                SelectExpression = c.SelectExpression,
                Converted = c.Converted,
                Style = c.Style,
                NumericFormat = Enum.TryParse<NumericFormat>(c.NumericFormat, out var format) ? format : null,
            }).ToList();
        }

        var resolved = ColumnTransformResolver.Resolve(targetColumns.Select(c => c.Name).ToList(), flow.Transform, inferred);
        var viewName = $"v_{flow.Target.Table.Name}";
        var ddl = TransformViewBuilder.Build(flow.Target.Table.Schema, viewName, flow.Target.Table.Schema, flow.Target.Table.Name, resolved);
        await ExecuteAsync(targetConnectionString, ddl, ct).ConfigureAwait(false);

        return new TransformViewResult { ViewName = viewName, Ddl = ddl, Columns = resolved };
    }

    /// <summary>
    /// The business key for the whole apply. When the key-match pass is active and matchKeys declares its own
    /// columns, those override the load keys (legacy flw.MatchKey.KeyColumns), so the target's unique index, the
    /// upsert match, and the delete-detection all key on the same columns. Otherwise the load keys are used.
    /// </summary>
    private static IReadOnlyList<string> EffectiveKeyColumns(IngestionFlow flow)
        => flow.Load.MatchKeysInSourceAndTarget && flow.MatchKeys.KeyColumns.Count > 0
            ? flow.MatchKeys.KeyColumns
            : flow.Load.KeyColumns;

    // Combinations the engine refuses, checked before any data work so a run fails fast instead of doing half
    // the job. The YAML loader rejects the same combinations at parse time; this is the equivalent gate for the
    // control-DB and legacy load paths, which never pass through that loader.
    //   - temporal + truncateBeforeLoad: SQL Server does not allow TRUNCATE TABLE on a system-versioned table.
    //     Legacy silently skipped the truncate, leaving the flow believing it had done a full reload when it
    //     had appended; failing loudly is the honest behavior.
    //   - temporal + scd2: two history mechanisms on one table record every change twice.
    //   - insertUnknownDimensionRow needs a defined sentinel-key convention plus exemption from the match-key
    //     delete pass and the upsert's key match, so it is a designed feature, not a silent best-effort insert.
    private static void EnsureSupportedFeatures(IngestionFlow flow)
    {
        if (flow.Versioning.Temporal.Enabled && flow.Target.TruncateBeforeLoad)
        {
            throw new SqlFlowException(
                "versioning.temporal cannot be combined with target.truncateBeforeLoad: SQL Server does not allow " +
                "TRUNCATE TABLE on a system-versioned table, and a full reload contradicts keeping a complete row history.");
        }

        if (flow.Versioning.Temporal.Enabled && flow.Versioning.Scd2.Enabled)
        {
            throw new SqlFlowException(
                "versioning.temporal and versioning.scd2 cannot both be enabled: system-versioned history and " +
                "engine-managed SCD2 history would record every change twice. Choose one.");
        }

        if (flow.Versioning.InsertUnknownDimensionRow)
        {
            throw new SqlFlowException(
                "insertUnknownDimensionRow is not yet implemented. Remove the flag; an unknown-member row must be " +
                "seeded explicitly for now (a designed implementation with match-key exemption is pending).");
        }
    }

    private static IReadOnlyList<LoadStatement> BuildLoadStatements(
        IngestionFlow flow,
        RelationalObject staging,
        IReadOnlyList<string> dataColumnNames,
        IReadOnlyList<SqlColumn> stagingColumns,
        IReadOnlyDictionary<string, string> nameMap,
        IReadOnlyDictionary<string, SqlDataType> targetColumnTypes,
        IRunEventSink events)
    {
        // Per-file replace (load.reloadColumn): purge the batch's datasets from the target, then insert the
        // batch. It supersedes the keyed upsert (nothing to update after the purge) and works with or without
        // key columns, so it is resolved before the keyless insert-all path below. The reload column is declared
        // with the SOURCE name; map it to the target name the same way the dataset column is.
        if (!string.IsNullOrWhiteSpace(flow.Load.ReloadColumn))
        {
            var reloadColumn = MapNameOrNull(nameMap, flow.Load.ReloadColumn!) ?? flow.Load.ReloadColumn!;
            return UpsertGenerator.GenerateStatements(flow.Target.Table, staging, new UpsertOptions
            {
                DataColumns = dataColumnNames,
                KeyColumns = EffectiveKeyColumns(flow),
                ReloadColumn = reloadColumn,
                SkipInsert = flow.Load.SkipInsertNew,
                InsertedDateColumn = flow.SystemColumns.InsertedDate ? "InsertedDate_DW" : null,
                RowStatusColumn = flow.SystemColumns.RowStatus ? "RowStatus_DW" : null,
                HashAlgorithm = string.IsNullOrWhiteSpace(flow.Change.HashType) ? HashKey.DefaultAlgorithm : flow.Change.HashType!,
            })
            .Select(ToLoadStatement)
            .ToList();
        }

        if (EffectiveKeyColumns(flow).Count == 0)
        {
            // Keyless flow: there is no key to match on, so append every staged row (the legacy keyless
            // insert-all). A keyless flow is append-only by nature, exactly as legacy.
            return [new LoadStatement(LoadKind.InsertAll, BuildInsertAll(flow.Target.Table, staging, dataColumnNames, flow.SystemColumns))];
        }

        // Change-detection exclusions: the flow's declared IgnoreColumnsInHash (source names mapped to target
        // names) plus every column whose type cannot be concatenated into the checksum. Excluded columns are
        // still copied by the upsert; they just do not count as "changed".
        var excludeFromChecksum = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ignored in flow.Change.IgnoreColumnsInHash)
        {
            if (MapNameOrNull(nameMap, ignored) is { } mapped)
            {
                excludeFromChecksum.Add(mapped);
            }
        }

        // How each checksummed column is stored on both sides, so change detection can compare the value the
        // target holds instead of two renderings of it.
        var checksumColumnTypes = new Dictionary<string, ChecksumColumnType>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in stagingColumns)
        {
            // SqlFlow-generated columns (file provenance, audit stamps, the surrogate-key hashes, and the SCD2
            // period columns) are rewritten on every load - a fresh FileName_DW, FileDate_DW, RowNumber_DW, and
            // so on - so hashing them would make every matched row look changed and force a no-op UPDATE each
            // run, needlessly dirtying pages and bloating differential and transaction-log backups. Exclude
            // them, and any column whose type cannot be concatenated into the checksum, exactly as the legacy
            // engine's IgnoreChecksumColumns / InvalidChecksumDataTypes rules did. The target's type disqualifies
            // a column just as the staging type does: CONCAT reads BOTH sides, so a target column stored as text
            // or xml cannot be hashed even when staging carries it as nvarchar.
            targetColumnTypes.TryGetValue(column.Name, out var targetType);
            if (IsSqlFlowSystemColumn(column.Name)
                || NonChecksumTypes.Contains(column.DataType.BaseType)
                || (targetType is not null && NonChecksumTypes.Contains(targetType.BaseType)))
            {
                excludeFromChecksum.Add(column.Name);
                continue;
            }

            // Both sides' types drive the comparison: the staged value is converted to the target's type when
            // the two differ (the pre-created target of a migrated source stores nchar(36) as uniqueidentifier,
            // datetime2 as datetime), and a lossy family is rendered with an explicit style. Types SqlFlow does
            // not model (CLR/UDT and friends) are left alone: CONVERT cannot express them.
            if (targetType is not null && targetType.Family != SqlTypeFamily.Other)
            {
                checksumColumnTypes[column.Name] = new ChecksumColumnType { Staging = column.DataType, Target = targetType };
            }
        }

        if (excludeFromChecksum.Count > 0)
        {
            events.Log(RunLogLevel.Debug, "upsert.plan", "excluded from change detection: " + string.Join(", ", excludeFromChecksum.Order(StringComparer.OrdinalIgnoreCase)));
        }

        var restyped = checksumColumnTypes
            .Where(p => !string.Equals(p.Value.Staging.Render(), p.Value.Target.Render(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (restyped.Count > 0)
        {
            events.Log(RunLogLevel.Debug, "upsert.plan", "change detection compares these as the target stores them: "
                + string.Join(", ", restyped.Select(p => $"{p.Key} {p.Value.Staging.Render()} -> {p.Value.Target.Render()}")));
        }

        // SCD2 tracked attributes are declared with SOURCE names; map them to target names like the hash
        // exclusions. Empty means "every comparable non-key column" (the generator's default).
        var scd2 = flow.Versioning.Scd2;
        var scd2Tracked = scd2.Enabled
            ? scd2.TrackedColumns.Select(c => MapNameOrNull(nameMap, c)).Where(c => c is not null).Select(c => c!).ToList()
            : (IReadOnlyList<string>)[];

        // Keyed flow: always apply through the two-step upsert (or the SCD2 close+insert when versioning is on).
        // Its INSERT is an anti-join (WHERE NOT EXISTS on the keys) over staging collapsed to one row per key, so
        // it can never duplicate an existing target row (regardless of target state: empty, partially loaded, or a
        // full reload over a NULL watermark) NOR insert two same-key rows from a single staging batch (which an
        // incremental read over an append-mode landing source produces: the same key across several file loads),
        // while still reconciling changed rows. This deliberately improves on the legacy full-load branch, which
        // did a blind insert that duplicated rows when the target watermark was NULL.
        return UpsertGenerator.GenerateStatements(flow.Target.Table, staging, new UpsertOptions
        {
            DataColumns = dataColumnNames,
            KeyColumns = EffectiveKeyColumns(flow),
            SkipUpdate = flow.Load.SkipUpdateExisting,
            SkipInsert = flow.Load.SkipInsertNew,
            HashAlgorithm = string.IsNullOrWhiteSpace(flow.Change.HashType) ? HashKey.DefaultAlgorithm : flow.Change.HashType!,
            InsertedDateColumn = flow.SystemColumns.InsertedDate ? "InsertedDate_DW" : null,
            UpdatedDateColumn = flow.SystemColumns.UpdatedDate ? "UpdatedDate_DW" : null,
            RowStatusColumn = flow.SystemColumns.RowStatus ? "RowStatus_DW" : null,
            ExcludeFromChecksum = excludeFromChecksum,
            ChecksumColumnTypes = checksumColumnTypes,
            BatchToAvoidLockEscalation = flow.Load.BatchUpsertToAvoidLockEscalation,
            BatchRowCount = flow.Load.BatchUpsertRowCount,
            Scd2Enabled = scd2.Enabled,
            Scd2ValidFromColumn = scd2.Enabled ? scd2.ValidFromColumn : null,
            Scd2ValidToColumn = scd2.Enabled ? scd2.ValidToColumn : null,
            Scd2CurrentFlagColumn = scd2.Enabled ? scd2.CurrentFlagColumn : null,
            Scd2TrackedColumns = scd2Tracked,
            Scd2AsOfLiteral = scd2.Enabled ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : null,
            DataSetColumn = string.IsNullOrWhiteSpace(flow.Load.DataSetColumn)
                ? null
                : MapNameOrNull(nameMap, flow.Load.DataSetColumn!) ?? flow.Load.DataSetColumn,
        })
        .Select(ToLoadStatement)
        .ToList();
    }

    private static LoadStatement ToLoadStatement(UpsertStatement s)
        => new(
            s.Kind switch
            {
                UpsertStatementKind.Update => LoadKind.Update,
                UpsertStatementKind.Combined => LoadKind.UpsertLoop,
                UpsertStatementKind.Purge => LoadKind.Purge,
                _ => LoadKind.Insert,
            },
            s.Sql,
            s.CountFromScalar,
            s.CountFromResultSet);

    private static string BuildInsertAll(RelationalObject target, RelationalObject staging, IReadOnlyList<string> dataColumns, SystemColumnsPolicy system)
    {
        var insertColumns = dataColumns.Select(c => $"[{Escape(c)}]").ToList();
        var selectColumns = dataColumns.Select(c => $"src.[{Escape(c)}]").ToList();
        if (system.InsertedDate)
        {
            insertColumns.Add("[InsertedDate_DW]");
            selectColumns.Add("SYSUTCDATETIME()");
        }

        return $"INSERT INTO {SchemaQualified(target)} ({string.Join(", ", insertColumns)}) " +
               $"SELECT {string.Join(", ", selectColumns)} FROM {SchemaQualified(staging)} AS src;";
    }

    private async Task<long> BulkCopyAsync(
        ResolvedConnection source,
        string sourceSelect,
        string targetConnectionString,
        RelationalObject staging,
        IReadOnlyList<(string Source, string Target)> columns,
        IngestionLoadPolicy load,
        CancellationToken ct)
    {
        await using var sourceConnection = await _factory.OpenAsync(source, ct).ConfigureAwait(false);
        return await StreamSelectToStagingAsync(sourceConnection, sourceSelect, targetConnectionString, staging, columns, load, ct).ConfigureAwait(false);
    }

    // InitLoad fills the SAME staging table with one chunked SELECT per segment, fanned out under a
    // concurrency cap. Each segment uses its own source and target connection (SqlBulkCopy is safe concurrently
    // into one heap). The exact SqlBulkCopy.RowsCopied sum is the staged count (no estimated COUNT needed).
    private async Task<long> BulkCopyInitLoadAsync(
        ResolvedConnection source,
        string targetConnectionString,
        RelationalObject staging,
        IReadOnlyList<(string Source, string Target)> columns,
        IReadOnlyList<InitLoadSegment> segments,
        IngestionLoadPolicy load,
        CancellationToken ct)
    {
        if (segments.Count == 0)
        {
            return 0;
        }

        var maxDegree = Math.Max(1, load.Threads ?? 1);
        using var gate = new SemaphoreSlim(maxDegree, maxDegree);
        long total = 0;

        var tasks = segments.Select(async segment =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var sourceConnection = await _factory.OpenAsync(source, ct).ConfigureAwait(false);
                var copied = await StreamSelectToStagingAsync(sourceConnection, segment.Sql, targetConnectionString, staging, columns, load, ct).ConfigureAwait(false);
                Interlocked.Add(ref total, copied);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return total;
    }

    private static async Task<long> StreamSelectToStagingAsync(
        DbConnection sourceConnection,
        string sourceSelect,
        string targetConnectionString,
        RelationalObject staging,
        IReadOnlyList<(string Source, string Target)> columns,
        IngestionLoadPolicy load,
        CancellationToken ct)
    {
        await using var sourceCommand = sourceConnection.CreateCommand();
        sourceCommand.CommandText = sourceSelect;
        sourceCommand.CommandTimeout = 0;
        await using var reader = await sourceCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);

        await using var targetConnection = new SqlConnection(BulkTuning.ForBulk(targetConnectionString));
        await targetConnection.OpenAsync(ct).ConfigureAwait(false);

        // TABLOCK + a heap staging table + one batch (BatchSize 0) is the minimally-logged bulk path; the bulk
        // update lock it takes is mutually compatible, so the parallel segment streams above load the same
        // staging table concurrently without row/page-lock contention. The BatchUpsertRowCount knob
        // belongs to the APPLY step's key windows, not the staging load.
        using var bulkCopy = new SqlBulkCopy(targetConnection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            DestinationTableName = SchemaQualified(staging),
            BulkCopyTimeout = 0,
            EnableStreaming = true,
            BatchSize = 0,
        };
        foreach (var (sourceName, targetName) in columns)
        {
            bulkCopy.ColumnMappings.Add(sourceName, targetName);
        }

        await bulkCopy.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        return bulkCopy.RowsCopied;
    }

    // The source read always uses a `WHERE 1=1` base so the incremental and filter fragments (each beginning
    // with " AND ", per the legacy raw-append contract) compose onto it. The text executes on the SOURCE, so
    // its identifiers come from the source dialect.
    private static string BuildSourceSelect(RelationalObject sourceTable, IReadOnlyList<(string Source, string Target)> columns, string sourceWhere, ISourceSqlDialect dialect)
    {
        var columnList = string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Source)));
        return $"SELECT {columnList} FROM {dialect.QualifyObject(sourceTable)} WHERE 1=1{sourceWhere}";
    }

    // The default apply wraps both statements in one transaction (all-or-nothing). The batched apply runs
    // WITHOUT an enclosing transaction by design: its whole purpose is that each key window commits and
    // releases its locks on its own, trading atomicity for lock friendliness (the legacy contract of
    // BatchUpsertToAvoidLockEscalation). A batched script reports its total as a scalar result.
    private static async Task<(long Inserted, long Updated, long Deleted)> ApplyLoadAsync(
        string connectionString, IReadOnlyList<LoadStatement> statements, bool useTransaction, CancellationToken ct)
    {
        if (statements.Count == 0)
        {
            return (0, 0, 0);
        }

        long inserted = 0;
        long updated = 0;
        long deleted = 0;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var transaction = useTransaction
            ? (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        try
        {
            foreach (var statement in statements)
            {
                try
                {
                    await using var command = new SqlCommand(statement.Sql, connection, transaction) { CommandTimeout = 0 };

                    // The dataset-partitioned loop reports both totals as one row with Inserts and Updates columns.
                    if (statement.CountFromResultSet)
                    {
                        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                        if (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            inserted += Convert.ToInt64(reader["Inserts"], CultureInfo.InvariantCulture);
                            updated += Convert.ToInt64(reader["Updates"], CultureInfo.InvariantCulture);
                        }

                        continue;
                    }

                    long affected;
                    if (statement.CountFromScalar)
                    {
                        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                        affected = scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    if (statement.Kind == LoadKind.Update)
                    {
                        updated += affected;
                    }
                    else if (statement.Kind == LoadKind.Purge)
                    {
                        deleted += affected;
                    }
                    else
                    {
                        inserted += affected;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Surface exactly which load statement broke so the runner marks the precise trace entry (the
                    // batch traced them all up front). The outer catch still rolls back the enclosing transaction.
                    throw new LoadStatementException(statement, ex);
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch when (transaction is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        return (inserted, updated, deleted);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Task DropStagingAsync(string connectionString, RelationalObject staging, CancellationToken ct)
        => ExecuteAsync(connectionString, $"DROP TABLE IF EXISTS {SchemaQualified(staging)};", ct);

    /// <summary>The declared type of every column of a table, keyed by column name. An absent table (the target
    /// this run is about to create, or one a keyless flow never matches against) yields an empty map, which the
    /// callers read as "nothing to reconcile": a target SqlFlow creates carries the staging types by
    /// construction.</summary>
    private static async Task<IReadOnlyDictionary<string, SqlDataType>> ReadColumnTypesAsync(
        ICatalogReader catalog, string connectionString, RelationalObject table, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var introspected = await catalog.IntrospectObjectAsync(connection, ToName(table), ct).ConfigureAwait(false);
        return introspected is null
            ? new Dictionary<string, SqlDataType>(StringComparer.OrdinalIgnoreCase)
            // Evolvable columns only: a period column is GENERATED ALWAYS, so it is never staged, never
            // compared, and never written; letting it into the change-detection type map would be meaningless.
            : CatalogSchemaAdapter.ToEvolvableColumns(introspected)
                .ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised by <see cref="ApplyLoadAsync"/> when one load statement fails, carrying the offending
    /// statement so the run's failure is attributed to the exact trace entry rather than "the last one". The
    /// message is the underlying error verbatim, so the run's recorded error is unchanged.</summary>
    private sealed class LoadStatementException(LoadStatement statement, Exception inner)
        : Exception(inner.Message, inner)
    {
        public LoadStatement Statement { get; } = statement;
    }

    // The connection is already on the source database, so introspection uses the current-database OBJECT_ID
    // path (Database left null) rather than switching context.
    private static ThreePartName ToName(RelationalObject relationalObject)
        => new() { Database = null, Schema = relationalObject.Schema, Name = relationalObject.Name };

    private static IReadOnlySet<string> KeySet(IReadOnlyList<string> keys)
        => new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    // Canonical indexes reference the cleaned TARGET column names; the flow declares SOURCE names, so map
    // through the bulk-copy name map (identity when column cleaning is off). A required key column that is
    // not a bulk-copied target column (because it is ignored or virtual) is a contradictory configuration and
    // fails fast with a clear message rather than emitting an index on a phantom column.
    private static string MapName(IReadOnlyDictionary<string, string> nameMap, string source)
        => nameMap.TryGetValue(source, out var target)
            ? target
            : throw new SqlFlowException(
                $"Key column '{source}' is not a bulk-copied target column (is it in IgnoreColumns or a virtual column?).");

    // For an OPTIONAL canonical index (date / dataset), a column that is not a real bulk-copied target column
    // simply yields no index, rather than failing the run.
    private static string? MapNameOrNull(IReadOnlyDictionary<string, string> nameMap, string? source)
        => !string.IsNullOrWhiteSpace(source) && nameMap.TryGetValue(source, out var target) ? target : null;

    private static bool HasColumn(IReadOnlyList<SqlColumn> columns, string name)
        => columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // The canonical work-table name for a flow: the target it feeds plus the flow id, e.g. arc_Orders_279975153
    // (one table per flow, rebuilt by every run, so executions never accumulate per-run copies and the owner is
    // readable off the name). The match-key table passes "mkey" so it stays distinguishable from the staging
    // table beside it in the raw schema. Characters outside A-Z/a-z/0-9/_ fold to '_' so any bracketed target
    // identifier yields a plain name, and the traceable middle is trimmed when the composed name would exceed
    // the 128-character identifier cap; the flow-id suffix is what guarantees uniqueness, so it is never trimmed.
    private static string CanonicalWorkTableName(string? prefix, IngestionFlow flow)
    {
        var lead = string.IsNullOrEmpty(prefix) ? string.Empty : prefix + "_";
        var middle = new string(
            $"{flow.Target.Table.Schema}_{flow.Target.Table.Name}"
                .Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')
                .ToArray());
        var suffix = $"_{flow.FlowId}";
        var budget = MaxIdentifierLength - lead.Length - suffix.Length;
        if (middle.Length > budget)
        {
            middle = middle[..budget];
        }

        return $"{lead}{middle}{suffix}";
    }

    private static string SchemaQualified(RelationalObject relationalObject)
        => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    // The single high-water column that governs the consolidation gate (load.truncateSourceWhenConsolidated):
    // the first incremental column, else the date column. Null when the flow declares neither, which the run-start
    // guard rejects. Both landing and target carry this column under the same name in the chained landing pattern
    // (FileDate_DW and its siblings are clean, un-renamed system columns), so it addresses both sides directly.
    private static string? ConsolidationWatermarkColumn(IngestionFlow flow)
        => flow.Incremental.Columns.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
           ?? (string.IsNullOrWhiteSpace(flow.Incremental.DateColumn) ? null : flow.Incremental.DateColumn);

    // The [pre] landing table behind a chained flow's source view [pre].[v<Table>]: the same database and schema
    // with the leading "v_" view prefix stripped. A source that is already a base table (no prefix) is returned
    // unchanged, so the truncate targets it directly.
    private static RelationalObject LandingTableOf(RelationalObject sourceObject)
        => sourceObject.Name.StartsWith("v_", StringComparison.OrdinalIgnoreCase)
            ? sourceObject with { Name = sourceObject.Name[2..] }
            : sourceObject;

    // MAX(column) from a two-part-qualified table on the given connection, or null when the table is empty (the
    // scalar is NULL). Used to compare the landing and target high-water marks across their two databases.
    private static async Task<object?> ReadMaxAsync(string connectionString, string qualifiedTable, string column, string? whereClause, CancellationToken ct)
    {
        // The optional clause is the flow's incrementalClause verbatim (an "AND ..." fragment, same contract as
        // the watermark window probe), appended behind WHERE 1=1 so the fragment composes without parsing it.
        var filter = string.IsNullOrWhiteSpace(whereClause) ? string.Empty : $" WHERE 1=1 {whereClause.Trim()}";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand($"SELECT MAX([{Escape(column)}]) FROM {qualifiedTable}{filter};", connection) { CommandTimeout = 0 };
        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? null : scalar;
    }

    // Order two high-water marks read from the same (schema-synced) column. The numeric family is normalized to
    // decimal so an int-vs-decimal skew between the two MAX reads still orders correctly; other comparable types
    // (datetime2/date, string) compare directly. Both arguments are non-null at the single call site.
    private static int CompareWatermarks(object left, object right)
        => IsNumeric(left) && IsNumeric(right)
            ? Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture))
            : ((IComparable)left).CompareTo(right);

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    // A culture-invariant rendering of a watermark for the run log (so a decimal FileDate_DW or a datetime2 reads
    // the same regardless of the runner's locale); "(empty)" for the absent (target-not-yet-loaded) mark.
    private static string Describe(object? watermark)
        => watermark switch
        {
            null => "(empty)",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => watermark.ToString() ?? "(empty)",
        };

    /// <summary>The run's wall time in seconds, to milliseconds. It used to truncate to whole seconds, which made
    /// every run under a second report "0s" and, because the rate divides by it, "0 rows/s" no matter how many rows
    /// it moved. Millisecond resolution matches what the acquire and file runners already report.</summary>
    private static double DurationSeconds(DateTime startUtc, DateTime endUtc)
        => Math.Round(Math.Max(0, (endUtc - startUtc).TotalSeconds), 3);

    /// <summary>Rows per second over the run's wall time. Zero only when the clock genuinely reports no elapsed
    /// time, which no run that staged a row can do at millisecond resolution: a zero rate now means zero rows.</summary>
    private static decimal FlowRateOf(long rowsFetched, double durationSeconds)
        => durationSeconds > 0 ? Math.Round((decimal)rowsFetched / (decimal)durationSeconds, 2) : 0m;

    /// <summary>The legacy run log's DurationSec column is a whole number of seconds (the ported flw.SysLog
    /// contract), so the precise duration is rounded at that boundary and nowhere else.</summary>
    private static int WholeSeconds(double durationSeconds)
        => (int)Math.Round(Math.Max(0, durationSeconds), MidpointRounding.AwayFromZero);

    /// <summary>Durations and rates render invariantly, so a node running under a comma-decimal locale writes the
    /// same run log as every other node.</summary>
    private static string Fmt(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The incremental read scope this run resolved to, for the run detail: the operator's full-load /
    /// backfill substitution takes precedence (matching the resolver's own precedence), then the init-load chunk
    /// plan, then the watermark-derived window. The filter is the exact source WHERE the read used; the watermark
    /// is the value the MAX/MIN probe returned and which object it came from.</summary>
    private static IncrementalSummary BuildIncrementalSummary(IngestionFlow flow, IngestionRunOptions options, IncrementalWindow window)
    {
        var filter = window.SourceWhere.Length > 0 ? $"WHERE 1=1{window.SourceWhere}" : null;

        if (options.Parameters.FullLoad)
        {
            return new IncrementalSummary { Mode = IncrementalModes.Full, Filter = "full load (watermark bypassed by run parameter)" };
        }

        if (options.Parameters.BackfillFrom is not null || !string.IsNullOrWhiteSpace(options.Parameters.SourceFilter))
        {
            return new IncrementalSummary { Mode = IncrementalModes.Backfill, Filter = filter };
        }

        if (flow.InitLoad.Enabled)
        {
            var from = flow.InitLoad.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "beginning";
            var to = flow.InitLoad.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "now";
            return new IncrementalSummary { Mode = IncrementalModes.InitLoad, Filter = $"init-load window {from} .. {to}" };
        }

        return new IncrementalSummary
        {
            Mode = filter is not null ? IncrementalModes.Incremental : IncrementalModes.Full,
            Filter = filter ?? "full read (no incremental bound)",
            Watermark = window.Watermark,
            WatermarkSource = window.WatermarkSource,
        };
    }

    private static IngestionRunRecord BuildRunRecord(
        IngestionFlow flow,
        IngestionRunOptions options,
        Guid runId,
        DateTime startUtc,
        DateTime endUtc,
        int durationSeconds,
        long rowsFetched,
        long rowsInserted,
        long rowsUpdated,
        long rowsDeleted,
        bool success,
        string? error,
        string? selectCmd,
        string? insertCmd,
        string? updateCmd,
        string? createCmd,
        decimal flowRate,
        string? traceLog)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"{flow.Source.Server}.{flow.Source.Table.QualifiedName}-->{flow.Target.Server}.{flow.Target.Table.QualifiedName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = rowsFetched,
            RowsInserted = rowsInserted,
            RowsUpdated = rowsUpdated,
            RowsDeleted = rowsDeleted,
            FlowRate = flowRate,
            Success = success,
            Threads = flow.Load.Threads,
            SelectCmd = selectCmd,
            InsertCmd = insertCmd,
            UpdateCmd = updateCmd,
            CreateCmd = createCmd,
            TraceLog = string.IsNullOrEmpty(traceLog) ? null : traceLog,
            Error = error,
        };

    private enum LoadKind
    {
        Update,
        Insert,
        InsertAll,

        /// <summary>The dataset-partitioned loop: one script doing both branches, reporting its Inserts/Updates
        /// totals as a single result-set row.</summary>
        UpsertLoop,

        /// <summary>The per-file replace purge (load.reloadColumn): a set-based DELETE of the batch's datasets
        /// whose affected-row count is attributed to rows deleted, not inserted or updated.</summary>
        Purge,
    }

    private sealed record LoadStatement(LoadKind Kind, string Sql, bool CountFromScalar = false, bool CountFromResultSet = false);
}
