---
id: flow-batch
title: "Batch flow (flowType: batch): ordered multi-flow runs"
type: flow-reference
summary: "flowType: batch runs member flows in lineage-computed waves with include/exclude globs, onError stop|continue, ignoreErrors, maxParallel, and connect."
keywords:
  - batch
  - members
  - include globs
  - waves
  - maxparallel
  - onerror
  - ignoreerrors
  - connect
  - inactive
yamlPath: "(root, flowType: batch)"
related:
  - cli-run
  - concept-lineage-graph-and-plan
  - concept-architecture-and-execution
sourceRefs:
  - src/SqlFlow.Yaml/YamlBatchFlowLoader.cs
  - src/SqlFlow.Core/Batch/BatchFlow.cs
  - src/SqlFlow.Core/Batch/BatchRunResult.cs
  - src/SqlFlow.Orchestration/BatchOrchestrator.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.Cli/Program.cs
  - samples/seed/seed-batch.flow.yaml
---

# Batch flow (flowType: batch)

A batch flow runs a set of member flows as one ordered, wave-concurrent run. Lineage computes concurrency waves over the members: every member in a wave runs concurrently (bounded by `maxParallel`) and the whole wave must finish before the next wave starts. Members are selected by globs relative to the batch file's directory, failure handling is explicit (`onError`, `ignoreErrors`), and members can be deactivated for a run (`members.inactive`). A batch owns no connections of its own; each member carries its own. Every member runs through the same execution path (`src/SqlFlow.Execution/DocumentExecutor.cs`) as a directly-invoked flow.

Minimal working example (adapted from samples/seed/seed-batch.flow.yaml):

```yaml
flowType: batch
name: seed-batch

members:
  include:
    - orders-export.flow.yaml
```

Run it like any other flow:

```bash
sqlflow run samples/seed/seed-batch.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | (none) | Must be `batch` to select this document kind. |
| `name` | string | yes | (none) | The batch's flow name; also names its run-history folder. |
| `description` | string | no | (none) | Free-text description; blank values are treated as absent. |
| `members.include` | string list | yes | (none) | Globs, relative to the batch file's directory, selecting member flow files. Must be non-empty. |
| `members.exclude` | string list | no | `[]` | Globs removing files from the included set entirely. |
| `members.inactive` | string list | no | `[]` | Globs marking members that stay declared but are skipped this run. |
| `ignoreErrors` | string list | no | `[]` | Globs (over member flow files) whose failure is non-fatal. |
| `onError` | string | no | `stop` | `stop` or `continue`: what the batch does when a non-ignored member fails. |
| `maxParallel` | integer | no | `0` | Concurrency cap within a wave; `0` means unbounded (the whole wave at once). |
| `connect` | string | no | `auto` | `auto`, `always`, or `never`: whether lineage connects to the databases to derive ordering. |

Unknown keys are ignored by the loader (`IgnoreUnmatchedProperties`). Blank list entries are trimmed and dropped.

### name

Required. Validation error when missing or blank:

```text
<source>: 'name' is required for a batch flow (flowType: batch).
```

The name derives the stable flow id and names the run-history folder `.sqlflow/runs/<name>/`.

### members.include

Required and must contain at least one non-blank glob. Globs are matched, case-insensitively, against the flow files lineage discovered under the batch file's directory. Validation error when missing or empty:

```text
<source>: a batch needs at least one 'members.include' glob (relative to the batch file's directory).
```

Only files that lineage recognizes as flow documents can become members. Lineage's declared-tier collection (`src/SqlFlow.Lineage/Collection/FlowSetCollector.cs`) assigns no flow node to a batch document or to a file that fails to parse, so neither one is ever a candidate, even when an include glob is broad enough to match its file name. The shared per-document run entry point (`src/SqlFlow.Execution/DocumentExecutor.cs`) also refuses a batch document reached through the batch's own member-run path as a backstop, failing with:

```text
a batch cannot be a member of another batch.
```

If no flow files match at run time, the batch run fails with:

```text
no member flows matched include [<globs>] under '<directory>'.
```

A member flow name declared by more than one file hard-fails the batch, with the colliding files named:

```text
member flow name '<name>' is declared by <n> files (<files>); names must be unique within a batch.
```

### members.exclude

Globs removing files from the included set entirely. An excluded file is not a member at all: it is not reported, not ordered, and its absence does not block anything.

### members.inactive

Globs marking members that stay declared but are skipped this run. An inactive member is reported with status `Inactive` (wave 0) and never runs. Its dependents still run: an inactive member is assumed handled outside the batch, not failed.

### ignoreErrors

Globs, matched against member flow files the same way as `members.include`, marking members whose failure is non-fatal. When such a member fails, its status is `FailedIgnored`: the failure is recorded but never triggers `onError: stop` and never blocks the member's dependents. An ignored failure does not, by itself, fail the batch.

### onError

Allowed values (case-insensitive): `stop` (default) and `continue`. Validation error otherwise:

```text
<source>: 'onError' must be 'stop' or 'continue', got '<value>'.
```

- `stop`: when a non-ignored member fails, the current wave finishes (the wave barrier stays clean), then no further wave runs. Members in later waves are recorded as `Skipped` with the reason `batch stopped before this wave`.
- `continue`: every member that does not depend on the failed one still runs, across all remaining waves. Members that transitively depend on a failed (or skipped) member are recorded as `Skipped` with the reason `depends on '<member>', which did not succeed`.

### maxParallel

The maximum number of members running at once within a wave. `0` (the default) means unbounded: the whole wave runs concurrently. Any positive number caps concurrency with a semaphore. Negative values fail validation:

```text
<source>: 'maxParallel' must be 0 (unbounded) or a positive number, got <n>.
```

### connect

Whether the batch connects to the databases so lineage can derive ordering from live module definitions (view and procedure bodies). Allowed values (case-insensitive):

- `auto` (default): connect only when a member needs it for correct ordering, which is when the member set contains a stored-procedure flow (`flowType: sp`), whose real reads and writes live in the module body rather than the document. In that case lineage is recomputed with the derived tier enabled.
- `always`: always derive ordering from live module definitions (this also catches view-on-view source chains).
- `never`: never connect; order from the declared and observed lineage tiers only.

Validation error otherwise:

```text
<source>: 'connect' must be 'auto', 'always', or 'never', got '<value>'.
```

## Execution model

- Lineage (`src/SqlFlow.Orchestration/BatchOrchestrator.cs`) computes the member dependency graph over the batch file's directory and assigns each active member a wave with a modified Kahn topological sort: a member's wave is one past its latest dependency's wave.
- Waves run in order. Within a wave every scheduled member runs concurrently through the shared `DocumentExecutor`, bounded by `maxParallel`; the whole wave must finish before the next starts.
- Members whose mutual order is a dependency cycle are never refused: they are placed together in a final fallback wave, listed under `unordered` in the result, and a warning is recorded (`dependency cycle among members (...); they run together in the final wave, order undecidable.`).
- Backfill run parameters passed on the `sqlflow run` command line apply to every member of the batch.

### Member statuses

Each member ends in exactly one `BatchMemberStatus` (`src/SqlFlow.Core/Batch/BatchRunResult.cs`):

| Status | Meaning |
| --- | --- |
| `Succeeded` | Ran and succeeded. |
| `Failed` | Ran and failed (not covered by `ignoreErrors`). |
| `FailedIgnored` | Ran and failed, but the member is in `ignoreErrors`: it neither stopped the batch nor blocked its dependents. |
| `Skipped` | Not run, because a member it depends on did not succeed or because the batch stopped before its wave. |
| `Inactive` | Declared in the batch but deactivated this run via `members.inactive`. |

### Batch success

The batch as a whole succeeds only when no member ended `Failed` and no member ended `Skipped`. `FailedIgnored` and `Inactive` members do not fail the batch.

## Artifacts and the shadow catalog

A batch run writes the canonical run-history artifacts to `.sqlflow/runs/<name>/<yyyyMMdd-HHmmss>_<runid-prefix>/` next to the batch file (the suffix is the first 8 characters of the run id):

- `run.json`: the uniform run artifact (kind `batch`).
- `run.log`: the rendered batch log.
- `trace.sql`: empty; a batch generates no SQL of its own, each member's trace is in its own run folder.
- `batch.json`: the full `BatchRunResult` (waves, every member's terminal state, unordered members, warnings, counts, duration).

Each member that ran also has its own run folder with its own artifacts. When a catalog database is configured (`--db`, or the `SQLFLOW_CATALOG_DB` environment variable), the batch run and every executed member run are recorded into the shadow catalog automatically; with neither configured, the run stays a pure file operation and no catalog write-back happens.

Example `batch.json` shape (from `samples/seed/.sqlflow/runs/seed-batch/`, a run of the committed seed sample where the include glob matches its one member, `orders-export.flow.yaml`, but that member fails immediately because the `SQLFLOW_DW` connection environment variable is not set):

```json
{
  "runId": "d8738a6f-4c2f-4952-99ef-108300965671",
  "success": false,
  "error": null,
  "batchName": "seed-batch",
  "onError": "Stop",
  "waves": [
    {
      "wave": 1,
      "members": ["orders-export-seed"]
    }
  ],
  "members": [
    {
      "flowName": "orders-export-seed",
      "flowKind": "exp",
      "file": "orders-export.flow.yaml",
      "wave": 1,
      "status": "Failed",
      "error": "Environment variable 'SQLFLOW_DW' is not set.",
      "runId": "6830c378-22a4-4fef-92bf-ef5a21c2ede9",
      "runDirectory": "samples/seed/.sqlflow/runs/orders-export-seed/20260703-080121_6830c378",
      "durationSeconds": 0
    }
  ],
  "unordered": [],
  "warnings": [],
  "succeeded": 0,
  "failed": 1,
  "skipped": 0,
  "inactive": 0,
  "durationSeconds": 0.502
}
```

## Full example

```yaml
flowType: batch
name: nightly-load
description: Load all raw tables, then run the downstream procedures.

members:
  include:
    - "flows/**/*.flow.yaml"
  exclude:
    - "flows/adhoc/*.flow.yaml"
  inactive:
    - "flows/raw/legacy-orders.flow.yaml"

ignoreErrors:
  - "flows/raw/optional-*.flow.yaml"

onError: continue
maxParallel: 4
connect: auto
```

Run it, echoing the result as JSON:

```bash
sqlflow run flows/nightly-load.flow.yaml --json
```

## See also

- [sqlflow run](../cli/run.md): how flows and batches are executed from the CLI.
- [Lineage graph and plan](../concepts/lineage-graph-and-plan.md): how the waves and member dependencies are computed.
- [Architecture and execution](../concepts/architecture-and-execution.md): the single execution path members share with direct runs.
