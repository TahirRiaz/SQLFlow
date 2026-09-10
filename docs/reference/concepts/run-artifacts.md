---
id: concept-run-artifacts
title: Run artifacts, logging, and diagnostics
type: concept
summary: Every run writes a durable .sqlflow/runs folder with run.json, run.log, and trace.sql; how the log levels, pruning, and tracing seams work.
keywords:
  - .sqlflow/runs
  - run.json
  - run.log
  - trace.sql
  - log levels
  - activitysource
  - pruning
  - healthcheck.json
  - batch.json
related:
  - cli-run
  - concept-shadow-catalog
  - concept-lineage-tiers
sourceRefs:
  - src/SqlFlow.Core/Runs/RunHistoryWriter.cs
  - src/SqlFlow.Core/Runs/RunArtifact.cs
  - src/SqlFlow.Core/Runs/RunLog.cs
  - src/SqlFlow.Core/Ingestion/SqlTrace.cs
  - src/SqlFlow.Core/Batch/BatchRunResult.cs
  - src/SqlFlow.Core/Diagnostics/SqlFlowDiagnostics.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Events/NullFlowEventSink.cs
  - src/SqlFlow.Execution/RunHistory.cs
  - src/SqlFlow.Execution/ExecutionJson.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.Execution/RunLogRenderer.cs
  - src/SqlFlow.Execution/SqlFlowEngineServices.cs
  - src/SqlFlow.HealthCheck/HealthCheckModelStore.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Cli/Program.cs
---

# Run artifacts, logging, and diagnostics

Every run of a flow document leaves a durable, inspectable record on disk: a per-run folder under `.sqlflow/runs/` next to the flow document, holding the run's result JSON, its canonical step-by-step log, and every SQL statement the engine generated. This is the without-database counterpart of the legacy `flw.SysLog`: a failed pipeline can be debugged from files alone, with no catalog database and no server access. The same artifacts also feed the observed lineage tier and the shadow catalog (`sqlflow db sync` projects `run.json` detail into it).

## The .sqlflow folder layout

`RunHistoryWriter` (src/SqlFlow.Core/Runs/RunHistoryWriter.cs) anchors the history next to the flow document:

```text
.sqlflow/
  runs/
    <flow-name>/
      <yyyyMMdd-HHmmss>_<run-id-prefix>/
        run.json
        run.log
        trace.sql
        healthcheck.json   (hc runs only)
        scm.json           (scm runs only)
        batch.json         (batch runs only)
  state/
    <flow-name>/
      <metric-name>/
        healthcheck.model
        healthcheck.model.json
  lineage/
    lineage.json
  env
```

- The run folder name is the run's UTC start time (`yyyyMMdd-HHmmss`) plus the first 8 hex characters of the run id (a `Guid` formatted `N`), so lexicographic name order is chronological.
- Flow names are made filesystem-safe by `RunHistoryWriter.SafeName`: the name is trimmed, then each remaining character invalid in a file name is replaced with `_`; an empty result becomes `flow`. The same rule names the `state/` folders, so a flow's runs and models always line up across subfolders.
- Health-check models persist under `.sqlflow/state/<flow>/<metric>/` as `healthcheck.model` (the serialized ML.NET transformer) with a `healthcheck.model.json` metadata sidecar; both are written through a temp file plus an atomic replace (src/SqlFlow.HealthCheck/HealthCheckModelStore.cs). Deleting a metric's state folder forces retraining under the default `auto` training policy; under `training: never` a missing model fails the run instead of retraining (`HealthCheckTrainingDecision.Resolve`).
- The repository `.gitignore` excludes the whole `.sqlflow/` folder (run artifacts, model state, and the local `.sqlflow/env` secrets file). Keep that rule in every repository that holds flow documents (docs/environment-variables.md).

The anchor is the flow document's directory (`RunHistory.Write` in src/SqlFlow.Execution/RunHistory.cs). One exception: the ad-hoc `sqlflow healthcheck` command has no document, so it writes the identical artifact set under the directory given by `--state-dir` (via `RunHistory.WriteAt`); without `--state-dir`, an ad-hoc health check keeps its model in memory and writes no history.

## The standard files per run

| File | Content | Written for |
|---|---|---|
| `run.json` | The `RunArtifact` envelope: header fields plus the full kind-specific result | every run |
| `run.log` | The canonical step-by-step log: timestamped and leveled for `ing`/`exp`/`sp`/`hc`/`inv`, a fixed-format summary for `file`/`scm`/`batch` | every run |
| `trace.sql` | Every generated SQL statement, in execution order | every run (empty for `inv`, `scm`, and `batch` runs) |
| `healthcheck.json` | The full scored series report | `hc` runs |
| `scm.json` | The source-control run result | `scm` runs |
| `batch.json` | The `BatchRunResult`: waves, member outcomes, counts | `batch` runs |

A batch generates no SQL of its own, so its `trace.sql` is empty; each member flow writes its own run folder (with its own `run.json` and `trace.sql`), and `batch.json` records every member's `runDirectory`.

### run.json: the RunArtifact envelope

`RunArtifact` (src/SqlFlow.Core/Runs/RunArtifact.cs) is a stable, versioned contract: every `run.json`, for every flow kind, carries the same top-level fields, so the flat files can be bulk-loaded into a database or fed into reporting without per-kind parsing.

| Field | Meaning |
|---|---|
| `schemaVersion` | Currently `1` (`RunArtifact.CurrentSchemaVersion`); breaking header changes increment it |
| `flowKind` | `file`, `ing`, `exp`, `sp`, `hc`, `inv`, `scm`, or `batch` |
| `flowName` | The flow document's `name` (required for every kind except ingestion, which falls back to the target table name when `name` is omitted) |
| `runId` | The run's GUID |
| `success` | Whether the run succeeded |
| `writtenUtc` | When the artifact was written |
| `error` | The failure message, or `null` |
| `host` | The machine that executed the run (defaults to `Environment.MachineName`), so history aggregated from many nodes stays attributable |
| `result` | The full kind-specific result object, serialized as-is (for example an `IngestionRunResult` or `FlowResult`) |
| `events` | The run's canonical event timeline: every progress, decision, and warning event the engine published while the run executed, uniform across flow kinds and serialized into `run.json` for `file`/`ing`/`exp`/`sp`/`hc`/`inv`/`scm` runs (empty for `batch`). An `scm` run narrates its stages (connect, script, git, write, commit, done), reports scripting progress per object category, and ends with a one-line summary of what it did. Generated SQL is not duplicated here (it lives in the result's `sqlTrace`); a reader treats an absent array as no events |

A real header, from samples/quickstart/.sqlflow/runs/orders/20260618-112108_019eda76/run.json:

```json
{
  "schemaVersion": 1,
  "flowKind": "file",
  "flowName": "orders",
  "runId": "019eda76-a012-77f7-8681-ddad7097dca9",
  "success": true,
  "writtenUtc": "2026-06-18T11:21:08.6832846Z",
  "error": null,
  "host": "3GJPTB4",
  "result": {
    "runId": "019eda76-a012-77f7-8681-ddad7097dca9",
    "flowName": "orders",
    "status": "Success",
    "rowsLoaded": 4
  }
}
```

All artifact JSON (`run.json`, `batch.json`, `healthcheck.json`, `scm.json`) and the CLI's `--json` output share one serializer configuration, `ExecutionJson.Options` (src/SqlFlow.Execution/ExecutionJson.cs): indented, camelCase property names, enums as strings. Every JSON the engine writes or prints is byte-for-byte consistent.

### run.log: the canonical run log

Every run writes a `run.log`, but the rendering differs by kind. `ing`, `exp`, `sp`, `hc`, and `inv` runs go through `RunLogger` (src/SqlFlow.Core/Runs/RunLog.cs), the leveled, timestamped format described below. `file`, `scm`, and `batch` runs instead render a fixed-format summary through `RunLogRenderer` (src/SqlFlow.Execution/RunLogRenderer.cs): an outcome line (for example `flow '<name>' run <run-id>: SUCCESS, <n> row(s) in <ms>ms`) followed by kind-specific detail, a file flow's per-operation timings, a batch's per-wave and per-member status lines, or a source-control run's repository and commit detail, with no timestamps and no level tags.

`RunLogger` collects timestamped events in order and renders them as `run.log`. It is thread-safe (parallel writers and init-load segments emit concurrently), optionally echoes each formatted line live (the CLI streams the log to the console as the run progresses), and the log survives failure: whatever was recorded up to the failure point is exactly what gets written.

Line format: `yyyy-MM-dd HH:mm:ss.fffZ LEVEL step message`, with the level padded to 5 characters and the step to 22. A multi-line message (SQL at trace level) continues on indented `    | ` lines, so the file stays both readable and greppable.

```text
2026-06-17 14:04:17.728Z INFO  run.start              ingestion 'orders-dw' (flow 2058323265, run 54a16f70): [TestDB].[dbo].[Orders] -> [TestDB].[dbo].[Orders_DW], staging [raw].[dbo_Orders_DW_2058323265]
2026-06-17 14:04:18.847Z INFO  source.introspect      11 source column(s), 11 bulk-copied
2026-06-17 14:04:19.703Z INFO  stage.copy             4 row(s) staged
2026-06-17 14:04:20.195Z INFO  upsert.apply           4 inserted, 0 updated
2026-06-17 14:04:20.212Z INFO  run.end                SUCCESS in 2s (2 rows/s)
```

`RunLogLevel` is cumulative:

| Level | Value | Adds |
|---|---|---|
| `Info` | 0 | The authoritative step-by-step account: what ran, in what order, with counts, durations, and the outcome |
| `Debug` | 1 | The engine's decisions: resolved windows, column mappings, checksum exclusions, apply-mode choices |
| `Trace` | 2 | Every generated SQL statement inline at its point in the timeline |

Events above the enabled level are dropped at the source. `IRunEventSink` is the seam the runners emit through; `NullRunEventSink.Instance` is the no-op default, so a library caller that wants no log pays nothing.

Ingestion step names (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs) include `run.start`, `source.introspect`, `staging.schema`, `staging.reset`, `staging.create`, `incremental.window`, `source.select`, `stage.copy`, `target.evolve`, `target.index.canonical`, `target.truncate`, `upsert.update`, `upsert.insert`, `upsert.apply`, `matchkeys`, `target.index.desired`, `assertion`, `staging.drop`, `transform.view`, and `run.end`.

### trace.sql: the generated SQL

Every runner captures each generated SQL statement unconditionally as a `SqlTraceEntry { Sequence, Step, Sql }` on the run result, on success and (especially) on failure: the generated SQL is the debugging surface of a metadata-driven run. One call site feeds two outputs: the statement lands in the ordered trace that becomes `trace.sql`, and it is emitted to the run log at `Trace` level, so a trace-level `run.log` shows every statement at its exact point in the timeline.

`SqlTrace.Render` (src/SqlFlow.Core/Ingestion/SqlTrace.cs) formats the file as a `-- [sequence] step` comment header followed by the statement. The first entries of an ingestion run's trace.sql:

```sql
-- [1] staging.schema
IF SCHEMA_ID(N'raw') IS NULL EXEC(N'CREATE SCHEMA [raw]');

-- [2] staging.reset
DROP TABLE IF EXISTS [raw].[dbo_Orders_DW_2058323265];

-- [3] staging.create
IF OBJECT_ID(N'[raw].[dbo_Orders_DW_2058323265]', N'U') IS NULL
BEGIN
    CREATE TABLE [raw].[dbo_Orders_DW_2058323265] (
        [OrderId] bigint NOT NULL,
        [Customer] varchar(255) NULL,
        [Amount] varchar(255) NULL,
        [OrderDate] varchar(255) NULL,
        [IsPaid] varchar(255) NULL,
        [FileName_DW] nvarchar(4000) NULL,
        [FileDate_DW] datetime2(7) NULL,
        [FileRowDate_DW] datetime2(7) NULL,
        [FileSize_DW] bigint NULL,
        [DataSet_DW] datetime2(7) NULL,
        [RowNumber_DW] bigint NULL
    );
END;

-- [4] source.select
SELECT [OrderId], [Customer], [Amount], [OrderDate], [IsPaid], [FileName_DW], [FileDate_DW], [FileRowDate_DW], [FileSize_DW], [DataSet_DW], [RowNumber_DW] FROM [dbo].[Orders] WHERE 1=1
```

## Pruning and failure semantics

- History is pruned on each write to the 50 most recent run folders per flow (`RunHistoryWriter.KeepRuns = 50`). Because the timestamp prefix makes lexicographic order chronological, pruning is a name sort; the oldest folders beyond the limit are deleted. A folder held open (a viewer, a virus scanner) is skipped and retried on the next prune. Pruning never fails the run.
- A history-write failure is never fatal: the run already finished, so an `IOException` or `UnauthorizedAccessException` surfaces as `WARN  could not write the run history: <message>` through the warning sink (the CLI wires it to stderr) and the run directory is reported as `null` (src/SqlFlow.Execution/RunHistory.cs).

## Configuration touchpoints

- `sqlflow run <flow.yaml>` writes the artifact folder next to the flow file and prints its path (`run log: ...`).
- `--log-level info|debug|trace` sets the `run.log` detail for `ing`, `exp`, `sp`, `hc`, and `inv` runs (default `info`); `file`, `scm`, and `batch` runs always render their fixed-format log regardless of `--log-level`. An unknown value fails with `Unknown --log-level '<value>'. Allowed: info, debug, trace.`
- `--show-sql` prints the generated SQL trace to the console after the run, in addition to writing `trace.sql`.
- `--json` prints the kind-specific result as JSON on stdout, using the same `ExecutionJson.Options` serializer as the artifacts; console echo of the live log is suppressed so stdout stays clean JSON.
- `sqlflow healthcheck --state-dir <dir>` persists health-check models and the full run artifact set under `<dir>` for ad-hoc runs; without it, models are in-memory and no history is written.
- `sqlflow db sync` projects `run.json` detail from these folders into the shadow catalog; `sqlflow lineage` reads run artifacts as the observed lineage tier and writes `.sqlflow/lineage/lineage.json`.

## Diagnostics beyond the run log

Two further seams exist for observing runs programmatically:

- **Activity tracing.** The file-flow engine (`FlowRunner`, src/SqlFlow.Core/Engine/FlowRunner.cs) emits `System.Diagnostics.Activity` spans (`flow.run`, `flow.plan`, and one span per traced operation) on the shared `ActivitySource` named `SqlFlow` (`SqlFlowDiagnostics.ActivitySourceName`, src/SqlFlow.Core/Diagnostics/SqlFlowDiagnostics.cs). Subscribe an `ActivityListener` or an OpenTelemetry exporter to that source name to observe timings and failures live.
- **Flow event sink.** `IFlowEventSink` is the progress-event seam for file flows: `NullFlowEventSink.Instance` discards events, and the engine wiring (`SqlFlowEngineServices.AddSqlFlowEngine`, src/SqlFlow.Execution/SqlFlowEngineServices.cs) registers `ConsoleFlowEventSink` by default. Runner-level logging for the other kinds goes through the run-event sink described above (`IngestionRunOptions.Events`).

## Example: inspect a run from files alone

```bash
sqlflow run ./flows/orders-dw.flow.yaml --log-level trace
```

After the run, the newest folder under `.sqlflow/runs/orders-dw/` holds everything needed to diagnose it, whether it succeeded or failed:

```bash
ls .sqlflow/runs/orders-dw/
cat .sqlflow/runs/orders-dw/20260617-140420_54a16f70/run.log
cat .sqlflow/runs/orders-dw/20260617-140420_54a16f70/trace.sql
```

`run.json` gives the machine-readable outcome (`success`, `error`, the full result), `run.log` gives the step timeline, and `trace.sql` gives the exact statements the engine generated, ready to replay in a query window. On a failed ingestion run the staging table is kept rather than dropped, and `run.end` logs `FAILED after <seconds>s: <message> (staging <table> kept)` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs), so the exact staged rows are still there to inspect alongside the trace.

## See also

- [sqlflow run](../cli/run.md)
- [The shadow catalog](./shadow-catalog.md)
- [Lineage tiers](./lineage-tiers.md)
