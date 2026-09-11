---
id: concept-architecture-and-execution
title: "Architecture: lightweight vs full mode and the single execution pathway"
type: concept
summary: Two modes, one engine. How DocumentExecutor, AddSqlFlowEngine, the control plane, and pull-based workers share a single execution code path.
keywords:
  - documentexecutor
  - addsqlflowengine
  - lightweight mode
  - full mode
  - control plane
  - workers
  - pull model
  - pools
related:
  - cli-run
  - cli-worker
  - concept-control-plane
  - flow-overview
sourceRefs:
  - docs/architecture.md
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.Execution/SqlFlowEngineServices.cs
  - src/SqlFlow.SqlServer/Ingestion/WithoutDatabaseIngestion.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - docs/environment-variables.md
---

# Architecture: lightweight vs full mode and the single execution pathway

SQLFlow is built around two principles that the rest of the system falls out of:

1. **Two modes, one engine.** Lightweight mode has no control database: the YAML file is the whole pipeline, with no memory and no persistent log. Full mode adds the shadow catalog, the control plane, scheduling, and lineage on top. The only difference between the modes is which providers are plugged into the same engine. Memory is the upgrade, not the entry fee.
2. **One execution pathway.** Every flow, no matter how it was started (CLI `run`, a batch member, a worker draining the queue), runs through the same `DocumentExecutor` built from the same `AddSqlFlowEngine` composition. There is exactly one engine wiring to maintain, and a flow runs identically wherever it is hosted.

## Lightweight vs full mode

Lightweight mode is the default the CLI composes. Its provider choices are visible in `SqlFlowEngineServices.AddSqlFlowEngine` (src/SqlFlow.Execution/SqlFlowEngineServices.cs):

| Seam | Lightweight registration | Effect |
|---|---|---|
| `IStateStore` | `NullStateStore` | No watermark memory between runs. |
| `IDataSourceStore` | `NullDataSourceStore` | An `@alias` connection reference requires full mode; inline strings and `${env:...}` / `${keyvault:...}` references resolve directly. |
| Ingestion run log | `NullIngestionRunLog` (via `WithoutDatabaseIngestion`) | No persistent run log; run artifacts still land on disk under `.sqlflow/runs/`. |

Full mode does not replace the engine; it layers the shadow catalog (`sqlflow db migrate|sync|status`), the control plane, the scheduler, and lineage on top of it, and swaps in DB-backed providers where the lightweight defaults are null objects.

Per docs/architecture.md (current status), the following are implemented today: the stateless engine end-to-end, the control/compute split (SqlFlow.ControlPlane plus SqlFlow.Node with the durable run queue, scheduler, node registry, and managed git sync), identity (catalog-backed users plus Microsoft Entra ID single sign-on with JIT provisioning and first-run bootstrap), and the React GUI over the control plane API (dashboard, runs, fleet, pipelines, schedules, repo sources, lineage explorer, search, user administration). The DB-is-source-of-truth sync model (`apply`/`export` with optimistic concurrency) described in the same document is roadmap, not shipped behavior.

## Control plane vs compute workers

The control plane decides what runs and when, and owns state: the run queue is an in-memory dispatcher inside the control plane process, journaled to the catalog with plain conditional updates (see [The control plane](control-plane.md)). Compute workers introspect, generate, and execute near the data. Workers **pull** work from the dispatcher over HTTP; they need no inbound connectivity, so they can run inside a customer's network and the data never leaves it.

The trigger contract is references-only. `POST /api/v1/runs` (src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs) accepts a repo id, a flow name, an optional target `pool`, an optional `commitSha` pin, and the optional per-run substitution parameters (`fullLoad`, `backfillFrom`, `backfillTo`, `filePattern`, `scope`, `batch`, and `assertionsOnly`, the last evaluating an ingestion flow's data-quality assertions against the current target without loading). A secret is never accepted in the request or echoed in the response; the executing node resolves every credential from its own environment. The endpoint validates the request (non-blank flow name, an active pipeline in the catalog, a plausible commit SHA, coherent run parameters), enqueues through `IRunDispatcher`, and returns `202 Accepted` with the run id and a `Location` header pointing at `GET /api/v1/runs/{runId}`.

A worker node is just the CLI in a poll loop:

```bash
sqlflow worker --url <control-plane> [--token <ref>] [--db <conn-ref>] [--pool a,b] [--poll-seconds N]
```

`RunWorkerAsync` in src/SqlFlow.Cli/Program.cs composes the same `AddSqlFlowEngine` services plus a scoped catalog context, an `HttpNodeTransport` to the control plane, and the shared `RunWorker` loop, so a worker run is byte for byte a CLI run. Behavior verified in code:

- `--url` defaults to `SQLFLOW_URL`; `--token` to `SQLFLOW_TOKEN` (then the credential `sqlflow login` stored), and must be a personal access token carrying the `node` scope.
- `--db` defaults to `${env:SQLFLOW_CATALOG_DB}`; the node reads run definitions and streams traces through it, never the queue.
- `--poll-seconds` defaults to 30 and is clamped to 1..60: how long the dispatcher holds a poll when nothing is available.
- `--pool` is a comma-separated list of the pools this node serves. A worker always takes untargeted runs; with `--pool` it additionally takes runs routed to those pools.
- Placement is the dispatcher's, so any number of concurrent workers is safe; each holds a lease on what it executes and reports outcomes under that lease's fence.
- The worker runs until Ctrl+C or SIGTERM, both intercepted to drain to a clean stop rather than killing the in-flight run.

The container image wraps the same command: deploy/docker/worker-entrypoint.sh translates `SQLFLOW_WORKER_POOL`, `SQLFLOW_WORKER_POLL_SECONDS` and `SQLFLOW_WORKER_DRAIN_SECONDS` into flags, leaving the control plane URL, the node token and the catalog connection to the CLI's own environment defaults so none of them appears in `ps` output. deploy/compose/docker-compose.yml runs one scalable `worker` service (`docker compose up -d --scale worker=3`); deploy/k8s/worker-pool.yaml scales worker pods with KEDA (one manifest per pool, scale-to-zero when the queue is dry).

## DocumentExecutor: the single execution pathway

`DocumentExecutor` (src/SqlFlow.Execution/DocumentExecutor.cs) runs one loaded flow document. The CLI's `run` verb and the batch orchestrator both go through it, so a batch member runs through identical code to a directly-invoked flow. `ExecuteAsync` dispatches on the document type:

| Document type | FlowKind | Runner composition |
|---|---|---|
| `FileFlowDocument` | `file` | `FlowRunner` from the DI container |
| `IngestionFlowDocument` | `ing` | `WithoutDatabaseIngestion.BuildRunner` |
| `ExportFlowDocument` | `exp` | `WithoutDatabaseExport.BuildRunner` |
| `StoredProcedureFlowDocument` | `sp` | `WithoutDatabaseStoredProcedure.BuildRunner` |
| `HealthCheckFlowDocument` | `hc` | `WithoutDatabaseHealthCheck.BuildRunner` |
| `InvokeFlowDocument` | `inv` | `WithoutDatabaseInvoke.BuildRunner` |
| `SourceControlFlowDocument` | `scm` | `WithoutDatabaseSourceControl.BuildService` |

Any other document type fails with `Cannot run document kind '<type>'.` A `BatchFlowDocument` reaching the batch entry point (`RunAsync`) fails with `a batch cannot be a member of another batch.`

Contract details, all verifiable in the source:

- **No console output.** The executor never writes to the console; the caller prints from the returned result. A live run-log echo is opt-in through `DocumentExecutionOptions.Echo`, and secret-hygiene and run-history warnings surface through an optional warning sink supplied at construction. The CLI and the worker both register `new DocumentExecutor(sp, Console.Error.WriteLine)`, wiring warnings to stderr; the engine-default registration has no sink, so warnings are silent unless the host opts in.
- **Flow naming.** An ingestion flow's name defaults to `SysAlias`, falling back to the target table name. Export, stored procedure, health check, and source control flows use `SysAlias`; invoke flows use `InvokeAlias`.
- **Execution mode.** YAML runs record `ExecMode = "cli"` in the runner options.
- **Run artifacts.** Every kind writes `run.json`, `run.log`, and `trace.sql` through `RunHistory.Write` to a `.sqlflow/runs/<flow>/` folder next to the pipeline file; an `hc` run adds `healthcheck.json`, an `scm` run adds `scm.json`.
- **Result shape.** `DocumentExecutionResult` carries `FlowName`, `FlowKind`, `Success`, `Error`, `RunId`, `RunDirectory`, `DurationSeconds`, the typed kind-specific `Result` object (what `--json` serializes), `SqlTraceText` (what `--show-sql` prints), and, for health checks only, `HealthCheckReport`. The batch orchestrator projects this down to the uniform `DocumentRunOutcome`.
- **Per-run parameters.** File flows apply backfill parameters by rewriting the same knobs the definition itself uses (`initFromFileDate`/`initToFileDate`, `srcFile`, `Incremental.FullLoad`), so the engine needs no second code path. Exports re-window the chunk plan (`FromDate`/`ToDate`). Kinds with no window surface (`sp`, `hc`, `inv`) log an explicit run-log notice that the parameters do not apply, rather than silently dropping them.

## Engine composition: AddSqlFlowEngine

`SqlFlowEngineServices.AddSqlFlowEngine` (src/SqlFlow.Execution/SqlFlowEngineServices.cs) is the single engine registration used by every host that runs flows: the CLI, the control plane (src/SqlFlow.ControlPlane/Program.cs), and worker nodes. One call registers:

- **Source readers:** `CsvSourceReader`, `XlsSourceReader`, `JsonSourceReader`, `XmlSourceReader`, `ParquetSourceReader`, `DuckDbSourceReader`, plus both `IFileStore` implementations (`LocalFileStore` and `AzureBlobFileStore`, which reads abfss/wasbs/https lake paths through the shared Azure credential) and `LocalFileLifecycle`.
- **SQL Server providers:** `SqlServerTypeMapper`, `SqlServerSchemaProvider`, `SqlServerDdlGenerator`, `SqlBulkLoader`, `SqlServerIndexManager`, `SqlServerDesiredIndexManager`, `SqlServerIncrementalProbe`.
- **Lightweight state and events:** `NullStateStore`, `ConsoleFlowEventSink`.
- **The secret chain:** `AzureCredentialFactory`, `EnvSecretProvider`, `AzureKeyVaultSecretProvider`, `SecretResolver`, and `AzureStorageCredentialProvider` (the same Azure auth intent drives Key Vault, invoke, and DuckDB cloud reads).
- **The connection registry:** the SQL Server provider registry concatenated with `SqlFlowSourceProviders.CreateRegistry()` (MySQL and PostgreSQL), feeding the canonicalizers, `ConnectionResolver`, `CompositeConnectionFactory`, `CompositeCatalogReaderFactory`, `CatalogService`, and `ComputeTaskExecutor` (the executor behind queued ad-hoc datasource compute tasks, resolving connections through the same registry); `NullDataSourceStore` as the lightweight `IDataSourceStore`.
- **The inference stack:** `SqlServerLocaleProvider`, `SqlServerColumnProfiler`, `TypeInferencer`, `SqlServerInferenceValidator`, `InferenceService`.
- **All YAML loaders** (`YamlFlowLoader` through `YamlBatchFlowLoader` and the dispatching `YamlDocumentLoader`, plus `InferSpecLoader`), **`FlowRunner`**, and **`DocumentExecutor`** (registered without a warning sink; a host attaches one by re-registering, since the later registration wins).

### Without-database ingestion composition

`WithoutDatabaseIngestion.BuildRunner` (src/SqlFlow.SqlServer/Ingestion/WithoutDatabaseIngestion.cs) builds a fully wired `IngestionFlowRunner` from the flow document's own `connections`, assertion definitions, and `invokes`, with no control database anywhere. The SQL Server provider set is built in; MySQL and PostgreSQL sources plug in by passing the SqlFlow.Providers registry. Both the CLI and the tests construct through here, so YAML execution has exactly one code path.

`IngestionFlowRunner`'s optional seams default to loud null objects rather than silent no-ops (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs):

- `NullIngestionRunLog`: logs nothing.
- `NullAssertionRunner`: asserts nothing.
- `NullSurrogateKeyExecutor`: generates no surrogate keys.
- `NullInvokeRunner`: a set `Pre`/`PostInvokeAlias` is surfaced as a clear error rather than silently skipped.
- A flow that turns on transform type inference with no inference service wired fails with: `transform.inferTypes is on, but no inference service is wired into this runner. Register one (the engine host does by default), or declare the transforms explicitly under transform.columns.`

## Configuration touchpoints

- **CLI commands:** `sqlflow run <pipeline.yaml>` (with `--full`, `--from`, `--to`, `--file-pattern` backfill parameters, `--json`, `--show-sql`, `--log-level info|debug|trace`), `sqlflow worker` (`--url`, `--token`, `--db`, `--poll-seconds`, `--pool`), `sqlflow db migrate|sync|status` for the shadow catalog.
- **Environment variables:** `SQLFLOW_URL` and `SQLFLOW_TOKEN` (a worker's control plane and node token), `SQLFLOW_CATALOG_DB` (the default catalog connection for `worker` and `db`), `SQLFLOW_CONN_<NAME>` (a bare connection alias in a document resolves this canonical family), `SQLFLOW_AZURE_AUTH` (the Azure auth mode behind the one credential factory). Container deployments add `SQLFLOW_WORKER_POOL` and `SQLFLOW_WORKER_POLL_SECONDS`, which the worker entrypoint maps to the CLI flags.
- **YAML:** connection references in flow documents use `${env:NAME}` or `${keyvault:vault/secret}` forms, or a bare alias resolving `${env:SQLFLOW_CONN_<NAME>}`. Secrets never rest in the document; `sqlflow validate` and `sqlflow run` print a hygiene warning whenever a document embeds a `Password=`-style literal, without echoing the value.
- **API:** `POST /api/v1/runs` triggers a run (requires the `operate` scope); `POST /api/v1/runs/{runId}/cancel` cancels one; `GET /api/v1/runs/{runId}` reflects it from queued through terminal.

## Example: the same flow, three hosts

A single flow file runs identically in all three settings because each one is the same `DocumentExecutor` over the same `AddSqlFlowEngine` wiring.

```bash
# 1. Lightweight: run directly from YAML, no control database anywhere.
sqlflow run flows/orders.yaml --show-sql

# 2. Full mode compute: this host polls the control plane's dispatcher for work.
export SQLFLOW_URL="https://sqlflow.example.com"
export SQLFLOW_TOKEN="sqlf_..."
export SQLFLOW_CATALOG_DB="Server=catalog;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True"
sqlflow worker --pool etl-eu

# 3. Compose stack: one scalable worker container, more compute with no other change.
docker compose -f deploy/compose/docker-compose.yml up -d --scale worker=3
```

Triggering the queued run the worker in step 2 picks up:

```json
{
  "repoId": "6f9d2c1e-3b7a-4c5d-9e8f-0a1b2c3d4e5f",
  "flowName": "orders",
  "pool": "etl-eu",
  "fullLoad": false,
  "backfillFrom": "2026-01-01T00:00:00",
  "backfillTo": "2026-02-01T00:00:00"
}
```

The response is `202 Accepted` with `{"runId": "...", "status": "queued"}` and a `Location` header of `/api/v1/runs/{runId}`. The dispatcher hands the run to the worker's parked poll at once, the worker stages the snapshotted YAML (or materializes the pinned commit), executes through `DocumentExecutor`, resolves every `${env:...}` reference from its own environment, and reports the outcome under the id the trigger returned.

## See also

- [sqlflow run](../cli/run.md)
- [sqlflow worker](../cli/worker.md)
- [Flow kinds overview](../flow/overview.md)
- [Connections and secret references](../flow/connections.md)
- [Batch flows](../flow/batch.md)
