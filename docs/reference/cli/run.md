---
id: cli-run
title: sqlflow run
type: cli-command
summary: "Execute one flow document (file, ing, exp, sp, hc, inv, scm, or batch), write its run artifacts, and record the run into the shadow catalog."
keywords:
  - run
  - execute
  - batch
  - json output
  - catalog write-back
  - backfill flags
cliCommand: run
related:
  - cli-plan
  - guide-incremental-and-backfill
  - concept-run-artifacts
  - flow-batch
  - concept-shadow-catalog
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.Execution/ExecutionJson.cs
  - src/SqlFlow.Execution/RunHistory.cs
  - src/SqlFlow.Core/Runs/RunHistoryWriter.cs
  - src/SqlFlow.Core/Runs/RunParameters.cs
  - src/SqlFlow.Orchestration/BatchOrchestrator.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
---

# sqlflow run

## Synopsis

```bash
sqlflow run <pipeline.flow.yaml>
            [--json] [--show-sql] [--log-level <info|debug|trace>]
            [--full] [--from <date>] [--to <date>] [--file-pattern <glob>] [--assertions-only]
            [--retrain] [--fail-on-anomaly]
            [--dry-run] [--no-push]
            [--db <conn-ref>] [--repo <name>] [--repo-url <url>] [--no-db-sync]
            [-v|--verbose]
```

Flags may appear before or after the file path; the parser separates value-taking options from the positional arguments regardless of order.

## Description

Executes one flow document. The document's `flowType` key selects the kind: a file flow (no `flowType`), `ing` (table-to-table ingestion), `exp` (file export), `sp` (stored procedure), `hc` (ML health check), `inv` (Azure invoke), `scm` (database source control), or `batch` (ordered multi-flow run).

Every non-batch kind runs through the single `DocumentExecutor.ExecuteAsync` pathway in src/SqlFlow.Execution/DocumentExecutor.cs, the same code path used by the batch orchestrator and worker nodes. A `flowType: batch` document diverts to `RunBatchAsync`: `BatchOrchestrator` computes lineage waves over the member flows and runs each wave concurrently through that same executor, so a batch member executes identically to a directly invoked flow.

Every run writes durable artifacts to a `.sqlflow/runs/` folder next to the flow file (see Run artifacts below), prints a kind-specific console summary, and, when a catalog database is configured, records the run into the shadow catalog automatically.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<pipeline.flow.yaml>` | yes | Path to the flow document to execute. Any of the eight document kinds; the kind is discriminated by the document's `flowType` key. Missing argument prints the usage text and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--json` | switch | off | Print the kind-specific result object as JSON to stdout and suppress the console summary, the live run-log echo, and the catalog write-back summary line. |
| `--show-sql` | switch | off | Print the rendered generated SQL (the run's SQL trace) to stdout after the run. Nothing is printed when the trace is empty (inv and scm runs, and batch documents, generate no SQL of their own). |
| `--log-level` | string | `info` | run.log detail level: `info`, `debug`, or `trace`. Applies to the kinds that log through the leveled run logger (ing, exp, sp, hc, inv); file and scm runs render fixed-format logs. Any other value fails with `Unknown --log-level '<value>'. Allowed: info, debug, trace.` |
| `--full` | switch | off | Backfill: ignore the watermark and read everything the definition selects. Mutually exclusive with `--from`/`--to`. |
| `--from <date>` | date | none | Backfill: low bound of an externally bounded window (inclusive, UTC). Invariant-culture parse, for example `2023-01-15` or `'2023-01-15 06:00:00'`. |
| `--to <date>` | date | none | Backfill: high bound of the window. Requires `--from` and must be after it. |
| `--file-pattern <glob>` | glob | none | Backfill: narrow a file flow's discovery to one glob for this run, for example `orders_2023-01*.csv`. 1 to 200 characters, no control characters. |
| `--assertions-only` | switch | off | ing only: evaluate the flow's declared data-quality assertions (auto and manual alike) against the current target and do nothing else, no source read, no staging, no load. Any other kind refuses the run. Mutually exclusive with `--full`, `--from`/`--to`, and `--file-pattern`. |
| `--retrain` | switch | off | hc only: train fresh anomaly models this run (sets the flow's training mode to `Always`). |
| `--fail-on-anomaly` | switch | off | hc only: exit 2 when the check succeeds but finds anomalies (CI gating). |
| `--dry-run` | switch | off | scm only: script the database and write the working tree without committing. |
| `--no-push` | switch | off | scm only: commit locally but do not push to the remote. |
| `--db <conn-ref>` | string | `SQLFLOW_CATALOG_DB` when set | Connection reference of the shadow-catalog database for post-run write-back. Resolved through the secret resolver, so `${env:NAME}` and `${keyvault:vault/secret}` references work. |
| `--repo <name>` | string | see description | Repo attribution for the write-back: `--repo`, else the `SQLFLOW_REPO` environment variable, else the flow file's folder name, else `default`. |
| `--repo-url <url>` | string | none | Optional repo remote URL recorded as catalog metadata. |
| `--no-db-sync` | switch | off | Skip the catalog write-back entirely, even when `--db` or `SQLFLOW_CATALOG_DB` is configured. |
| `-v`, `--verbose` | switch | off | Emit per-stage debug logging. |

## Backfill parameters

`--full`, `--from`, `--to`, `--file-pattern`, and `--assertions-only` form the typed per-run `RunParameters` contract (src/SqlFlow.Core/Runs/RunParameters.cs). They override the run, never the YAML definition. Validation happens before execution; an invalid combination prints `ERROR  <message>` to stderr and exits 1 with these exact messages:

- `assertionsOnly cannot be combined with fullLoad, a backfill window, or a file pattern: an assertions-only run reads no source data, so a selection override has nothing to apply to.`
- `fullLoad and a backfill window are mutually exclusive: full load ignores every bound; a window IS the bound.`
- `backfillTo must be after backfillFrom.`
- `backfillTo requires backfillFrom (an upper bound alone is not a window).`
- `filePattern must be 1 to 200 characters.`
- `filePattern must not contain control characters.`
- `--from '<value>' is not a date; use e.g. 2023-01-15 or '2023-01-15 06:00:00'.` (same shape for `--to`)

Per-kind semantics, as applied in src/SqlFlow.Execution/DocumentExecutor.cs:

- File flows: the window becomes the source's `initFromFileDate`/`initToFileDate` options (inclusive file-date bounds), `--file-pattern` becomes the `srcFile` glob, and `--full` (or an explicit window) suppresses the watermark probe for this run.
- Ingestion flows: the parameters are passed to the ingestion runner; the window bounds the incremental date column and `--full` ignores the watermark. `--assertions-only` diverts to the assertions-only path, which evaluates the flow's whole assertion list (auto and manual) against the current target and returns without reading the source, staging, or loading.
- `--assertions-only` on any non-ingestion kind hard-fails before execution with `assertionsOnly applies only to ingestion flows (flowType: ing): assertions are declared on and evaluated against an ingestion flow's target.`
- Export flows: `--from`/`--to` re-window the chunk plan's `FromDate`/`ToDate` for this run only; `--full` and `--file-pattern` have no export meaning and are acknowledged in run.log rather than silently dropped.
- sp, hc, and inv flows: no window or selection surface exists; supplied parameters are noted in run.log as inapplicable and the flow runs as defined.
- Batch documents: the parameters are passed to every member, so a batch backfill is one command, not N YAML edits.

## Run artifacts

Every run writes its artifacts to `.sqlflow/runs/<flow>/<yyyyMMdd-HHmmss>_<runid8>/` next to the flow file (src/SqlFlow.Core/Runs/RunHistoryWriter.cs):

| File | Written for | Content |
| --- | --- | --- |
| `run.json` | all kinds | The run envelope (flowKind, flowName, runId, success, writtenUtc, error) plus the full kind-specific result. |
| `run.log` | all kinds | The canonical step-by-step log at the selected `--log-level`. |
| `trace.sql` | all kinds | The generated SQL; empty for inv, scm, and batch runs (a batch's members carry their own traces). |
| `healthcheck.json` | hc | The full scored series report. |
| `scm.json` | scm | The full source-control result. |
| `batch.json` | batch | The full batch result (waves, member outcomes, warnings). |

History is pruned to the 50 most recent run folders per flow. A history-write failure is never fatal: it prints `WARN  could not write the run history: <reason>` and the run's outcome is unchanged. Artifacts are serialized with the canonical settings in src/SqlFlow.Execution/ExecutionJson.cs: indented, camelCase properties, enums as names.

## Console output

Without `--json`, each kind prints a one-line summary plus kind-specific detail:

| Kind | Summary shape |
| --- | --- |
| file | `OK  '<name>': N row(s) loaded; M DDL statement(s).` plus the per-operation trace table. |
| ing | `OK  '<name>': N row(s) staged, N inserted, N updated in Ds (R rows/s).` plus the incremental WHERE clause, retained staging table, index actions, assertion outcomes, and surrogate-key results. |
| exp | `OK  exported N row(s) to M file(s) in Ds` plus one `<path>  N row(s), M byte(s)` line per file. |
| sp | `OK  EXEC <Database.Schema.Procedure> completed in Ds` |
| hc | `OK  N anomalies across M metric(s) (<frequency>) in Ds` plus data-quality counts, per-metric model lines, every SHIFT finding, and the worst ANOMALY findings (capped at 10 per metric; the rest are in healthcheck.json). |
| inv | `OK  invoke '<alias>' completed in Ds (<stdout>)` |
| scm | `OK  '<database>': N object(s) scripted; A added, C changed, D deleted (<commit status>) in Ds.` plus the repository path, branch, and remote. |
| batch | `OK  batch '<name>': N wave(s); S succeeded, F failed, K skipped, I inactive (onError stop\|continue) in Ds.` plus one `wave N: <members>` line per wave, then `FAILED` / `FAILED (ignored)` / `SKIPPED` member lines with errors, then WARN lines. |

On failure, exp, sp, hc, inv, and scm print `FAILED  <error>` in place of the `OK` line; file and ing keep their counts on the `FAILED` summary line and print the error on a following indented `error: <message>` line. Every kind except the file flow then prints an indented `run log: <run directory>` line (an absolute path) pointing at its artifact folder (the file flow prints its trace inline instead).

With `--json`, stdout carries only the kind-specific result object (`FlowResult`, `IngestionRunResult`, `ExportRunResult`, `StoredProcedureRunResult`, `HealthCheckRunResult`, `InvokeResult`, `SourceControlResult`, or `BatchRunResult`), serialized indented with camelCase properties and enum names. The live run-log echo is disabled so stdout stays clean JSON.

Secret-hygiene and run-history warnings always go to stderr: the CLI overrides the engine's `DocumentExecutor` registration with a `Console.Error` warning sink (src/SqlFlow.Cli/Program.cs, `BuildServiceProvider`), so they never contaminate `--json` output.

## Batch runs

A `flowType: batch` document routes to `RunBatchAsync` (src/SqlFlow.Cli/Program.cs) and `BatchOrchestrator` (src/SqlFlow.Orchestration/BatchOrchestrator.cs):

- Lineage computes concurrency waves over the members selected by the batch's `members.include`/`exclude` globs; each wave runs concurrently, bounded by `maxParallel` (0 means unbounded), and the whole wave finishes before the next starts.
- Member console echo is suppressed; each member logs to its own `.sqlflow/runs` folder. Member file paths resolve relative to the batch document's directory.
- `--full`, `--from`, `--to`, and `--file-pattern` are passed to every member; `--log-level`, `--dry-run`, and `--no-push` also propagate.
- `onError: stop` (default) finishes the current wave then stops; `onError: continue` keeps running independent members and skips only those that depend on a failure; members matching `ignoreErrors` may fail without stopping the batch or blocking their dependents; `members.inactive` members are declared but skipped this run.
- A batch cannot be a member of another batch; such a member fails with `a batch cannot be a member of another batch.` A batch whose globs match nothing fails with `no member flows matched include [...] under '<dir>'.` A member flow name declared by multiple files hard-fails the batch with the colliding files named.
- Batch artifacts are written under the batch document: `run.json` (flowKind `batch`), `run.log`, an empty `trace.sql`, and `batch.json` (the full `BatchRunResult`). With `--json` the printed object is the `BatchRunResult`.

## Catalog write-back

After the run, when `--db` is passed or the `SQLFLOW_CATALOG_DB` environment variable is set, the CLI records the run into the shadow catalog (src/SqlFlow.Cli/Program.cs, `RecordRunsInCatalogAsync`; src/SqlFlow.Catalog/CatalogSync.cs, `RecordRunAsync`):

- With neither configured, the run is a pure file operation: no error, no output. `--no-db-sync` opts out entirely.
- The catalog schema is migrated before the first write, so the first configured run against an empty server just works.
- Each produced `run.json` is recorded along with an upsert of the one pipeline row that produced it; recording is idempotent (an already-recorded run is left untouched). The write-back also refreshes that pipeline's declared columns from its YAML and applies the run's detected transform-view columns unless the catalog already knows a newer run for the pipeline (latest run wins; a stale artifact cannot regress the snapshot).
- Lineage and execution waves are NOT recomputed; that remains `sqlflow db sync` work.
- A batch records the batch document's run plus every member run that executed, each in a fresh database context.
- Repo attribution: `--repo`, else `SQLFLOW_REPO`, else the primary flow file's folder name, else `default`; `--repo-url` is optional metadata.
- On success it prints an indented `catalog: N of M run(s) recorded into [<repo>].` line (suppressed under `--json`); at most 10 per-run warnings go to stderr regardless of `--json`.
- The write-back is best-effort: any failure prints `WARN  catalog write-back skipped (<redacted reason>); the run itself is unaffected. Run 'sqlflow db sync' to backfill.` and never changes the run's exit code. A corrupt or oversized `run.json` only skips the run recording; the pipeline upsert still commits.

## Examples

Run the quickstart file flow (samples/quickstart/orders.flow.yaml):

```bash
export SQLFLOW_DW="Server=localhost;Database=DW;Integrated Security=true;TrustServerCertificate=true"
sqlflow run samples/quickstart/orders.flow.yaml
```

```text
OK  'orders': 4 row(s) loaded; 1 DDL statement(s).
  trace:
    ok    source.columns              45.3 ms
    ok    target.introspect         1037.3 ms
    ok    schema.apply-ddl           140.5 ms
    ...
```

Same run as machine-readable JSON (stdout is only the `FlowResult` object):

```bash
sqlflow run samples/quickstart/orders.flow.yaml --json
```

Backfill one month of files through a file flow, bypassing the watermark for this run only:

```bash
sqlflow run samples/quickstart/orders.flow.yaml \
  --from 2026-01-01 --to 2026-02-01 --file-pattern "orders_2026-01*.csv"
```

Run an export flow and print the generated SELECTs afterwards:

```bash
sqlflow run samples/export/orders-export.flow.yaml --show-sql --log-level debug
```

Run a batch as a full reload; the backfill flag propagates to every member:

```bash
sqlflow run samples/seed/seed-batch.flow.yaml --full
```

```text
OK  batch 'seed-batch': 1 wave(s); 1 succeeded, 0 failed, 0 skipped, 0 inactive (onError stop) in 4.2s.
  wave 1: orders-export-seed
  run log: /work/samples/seed/.sqlflow/runs/seed-batch/20260702-081500_1a2b3c4d
```

Run a stored-procedure flow and record it into the shadow catalog under an explicit repo name:

```bash
sqlflow run samples/sp/refresh-marts.flow.yaml --db '${env:SQLFLOW_CATALOG_DB}' --repo warehouse
```

```text
OK  EXEC DW.dbo.usp_RefreshMarts completed in 2.1s
  run log: /work/samples/sp/.sqlflow/runs/refresh-marts/20260702-081611_5e6f7a8b
  catalog: 1 of 1 run(s) recorded into [warehouse].
```

## Exit behavior

| Condition | Exit code |
| --- | --- |
| Run succeeded (file, ing, exp, sp, inv, scm) | 0 |
| Run failed (any kind) | 1 |
| hc run succeeded, no `--fail-on-anomaly` | 0 |
| hc run succeeded, `--fail-on-anomaly` set and anomalies found | 2 |
| hc run failed | 1 |
| Batch: `result.Success` | 0, else 1 |
| Invalid backfill flags or unknown `--log-level` (ERROR on stderr) | 1 |
| Missing file argument (usage printed) | 1 |

A catalog write-back failure never changes the exit code.

## See also

- [sqlflow plan](plan.md)
- [Incremental loading and backfill](../guides/incremental-and-backfill.md)
- [Run artifacts](../concepts/run-artifacts.md)
- [Batch flows](../flow/batch.md)
- [The shadow catalog and run write-back](../concepts/shadow-catalog.md)
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
