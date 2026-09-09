---
id: flow-overview
title: "Anatomy of a flow file: the twelve document kinds"
type: flow-reference
summary: How the top-level flowType key selects one of twelve flow document kinds, what each kind contains, and how loading and validation behave.
keywords:
  - flowtype
  - document kinds
  - file flow
  - top-level keys
  - yaml anatomy
related:
  - flow-source
  - flow-ing
  - flow-batch
  - concept-flow-identity
  - cli-validate
sourceRefs:
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Yaml/YamlSourceControlFlowLoader.cs
  - src/SqlFlow.Yaml/YamlBatchFlowLoader.cs
  - src/SqlFlow.Core/SqlFlowException.cs
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Core/Runs/RunHistoryWriter.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.Execution/SqlFlowEngineServices.cs
  - src/SqlFlow.Orchestration/BatchOrchestrator.cs
  - src/SqlFlow.DuckDb/DuckDbSourceReader.cs
  - src/SqlFlow.Cli/Program.cs
---

# Anatomy of a flow file: the twelve document kinds

Every SQLFlow pipeline is a single YAML document. There is no control database and no registration step: the file is the whole pipeline. One root key, `flowType`, decides which of twelve document kinds the file is, and therefore which loader parses it, which keys are recognized, and which engine runs it. This page is the map: what the twelve kinds are, how dispatch works, what every kind shares, and where each kind's full key reference lives.

## The flowType discriminator

`YamlDocumentLoader.Parse` in src/SqlFlow.Yaml/YamlDocumentLoader.cs is the single entry point for loading any flow document. It first deserializes the document with a cheap probe that reads only the root `flowType` key (and the `schedule:` block), then delegates the full parse to the matching kind-specific loader. Matching is case-insensitive and the value is trimmed, so `flowType: ING` and `flowType: " ing "` both select the ingestion loader.

| flowType | Kind | Loader | What it does |
| --- | --- | --- | --- |
| (absent or empty) | file flow | `YamlFlowLoader` | Loads CSV/JSON/XML/XLS/Parquet files into a SQL Server table, with dynamic schema evolution |
| `ing` | ingestion | `YamlIngestionFlowLoader` | Table-to-table copy through the flow's canonical staging table in the `raw` schema: schema evolution, keyed upsert, incremental loading |
| `exp` | export | `YamlExportFlowLoader` | Exports a SQL Server table or view to CSV or Parquet files, optionally chunked by day/month/key windows |
| `sp` | stored procedure | `YamlStoredProcedureFlowLoader` | Executes one existing stored procedure (`EXEC`) on a resolved server |
| `inv` | invoke | `YamlInvokeFlowLoader` | Triggers an Azure Data Factory pipeline or Automation runbook and waits for it to finish |
| `hc` | health check | `YamlHealthCheckFlowLoader` | ML health check: learns per-date metric behavior and reports anomalies, level shifts, and missing data |
| `scm` | source control | `YamlSourceControlFlowLoader` | Scripts a SQL Server database's objects with SMO into a git working tree, commits, and pushes over HTTPS |
| `batch` | batch | `YamlBatchFlowLoader` | Ordered multi-flow run: lineage computes concurrency waves over member flows and runs them wave by wave |
| `api` | acquisition | `YamlAcquireFlowLoader` | Fetches from a third-party system over any transport (HTTP, SFTP, S3, Azure Table) and lands the raw payloads in the lake |
| `cpy` | copy | `YamlCopyFlowLoader` | Copies files byte-for-byte between storage endpoints (local disk, Azure Blob/ADLS, S3), with optional zip/unzip |
| `sftp` | SFTP transfer | `YamlSftpFlowLoader` | Downloads files from an SFTP server into the lake/local, or uploads the other way |
| `cal` | calendar | `YamlCalendarFlowLoader` | Generates a date dimension for a declared range from rules alone (no source) and merges it into a table |
| `trl` | translate | `YamlTranslateFlowLoader` | Maps a SQL query result through a declared JSON template into arbitrarily shaped documents, saves them to a destination, and optionally delivers the saved documents to a remote API |

Any other value fails fast at parse time with a `FlowValidationException` carrying the full menu, instead of a confusing downstream validation failure:

```text
<file>: unknown flowType '<x>'. Use 'ing' for a table-to-table ingestion flow, 'exp' for a file export, 'sp' for a stored-procedure flow, 'inv' for an ADF/Automation trigger, 'hc' for an ML health check, 'scm' for a database source-control snapshot, 'batch' for an ordered multi-flow batch, 'api' for a generic acquisition flow (HTTP / SFTP / Azure Table), 'cpy' for a file-copy flow (local / Azure storage / S3, with optional zip/unzip), 'cal' for a generated calendar dimension, 'trl' for a JSON translation flow (query result to shaped documents, optionally delivered to an API), or omit flowType for a file flow.
```

## What every document kind shares

### Loading and error contract

All the loaders build their YamlDotNet deserializer the same way: camelCase naming convention plus `IgnoreUnmatchedProperties`. Two consequences:

- Keys are camelCase in YAML (`preProcess`, `desiredIndexes`, `flowType`).
- A misspelled key is silently ignored, not rejected. `sourec:` does not error as an unknown key; the document then fails (or misbehaves) as if the key were absent. Run `sqlflow validate` after editing and check that the summary line reflects what you wrote.

Every validation failure is a `FlowValidationException` (a subclass of `SqlFlowException`, see src/SqlFlow.Core/SqlFlowException.cs) with the file path prefixed to the message. The shared failure shapes:

| Condition | Message |
| --- | --- |
| File does not exist | `Pipeline file not found: '<path>'.` |
| Document is empty | `<file>: the document is empty.` |
| YAML does not parse | `<file>: invalid YAML - <parser message>` |

### The schedule envelope

Every document kind may carry a top-level `schedule:` block. It is captured on the `FlowDocument` envelope record itself, not inside any kind-specific model, so all twelve kinds declare a schedule the same way. The engine never schedules anything; the control plane turns the declared schedule into runs.

```yaml
schedule:
  cron: "0 6 * * *"
  timezone: UTC        # default UTC
  enabled: true        # default true
  # intervalSeconds: 300   # alternative to cron
```

A `schedule:` block carrying neither `cron` nor `intervalSeconds` is treated as absent rather than stored as a broken schedule.

### The lifecycle declaration

Every document kind may carry a top-level `lifecycle:` key with one of two values, parsed case-insensitively by `YamlDocumentParts.ParseLifecycle` (src/SqlFlow.Yaml/YamlDocumentParts.cs):

```yaml
lifecycle: development   # production (default) | development
```

`production` (the default when the key is absent) is a live pipeline: when one of its runs fails, is cancelled, is skipped by an upstream failure, or succeeds with failed assertions, the control plane records a notification event and alerts the users who subscribed. `development` marks a pipeline under construction: it executes, schedules, and records run history exactly like a production flow, but it never generates notification events, so iterating on a half-built flow cannot page anyone. Promotion is a one-line change with no behavioral side effects beyond alerting.

The value is projected to `CatalogPipeline.Lifecycle` on every catalog sync and shown by `sqlflow pipeline <id>`. Any other value fails the load: `<file>: 'lifecycle' has unknown value '<x>'. Allowed: production, development.` A source-control (`scm`) document projects as a catalog pipeline like any other flow, so its lifecycle gates alerting normally. On `batch`, which declares no flow of its own and never becomes a pipeline row, the key is accepted for vocabulary consistency but has no gating effect.

### One load path, one executor

`validate` and `run` accept every kind through one load path: `DocumentLoader.Load` (src/SqlFlow.Execution/DocumentLoader.cs) wraps `YamlDocumentLoader` and applies the file-relative fixups and the secret-hygiene check. On `run`, `DocumentExecutor` (src/SqlFlow.Execution/DocumentExecutor.cs) dispatches the seven non-batch kinds to their engines; a batch document goes to the batch orchestrator, whose members run through that same `DocumentExecutor`, so a batch member loads and runs under exactly the same rules as a directly invoked flow. `plan` supports file flows only and rejects everything else with:

```text
ERROR  'plan' supports file flows; ingestion, export, and stored-procedure work is determined at run time against the live source.
```

Path fixups applied per kind:

- A file flow's `source.location` resolves against the document's directory at load time unless it is already rooted.
- An export flow's target path resolves against the document's directory at load time, skipped when the value contains `://` or is already rooted.
- An scm flow's `repository.path` resolves the same way at execution time in `DocumentExecutor`, with the same `://` and rooted-path bypass.

Every load, on `validate` and `run` alike, also runs the secret-hygiene check: a connection value that looks like an embedded credential produces a loud warning naming the canonical alternatives (`${env:NAME}`, `${keyvault:vault/secret}`, or a bare connection name). The value itself is never echoed.

### Run artifacts and flowKind

Every run writes its artifacts to a timestamped run folder under `.sqlflow/runs/<flow>/` next to the pipeline file: `run.json` (the result), `run.log` (the step-by-step log), and `trace.sql` (the generated SQL). A batch run adds `batch.json`; an hc run adds `healthcheck.json` (the full scored series); an scm run adds `scm.json`. The `flowKind` string recorded in `run.json` per kind:

| Document | flowKind |
| --- | --- |
| file flow | `file` |
| `flowType: ing` | `ing` |
| `flowType: exp` | `exp` |
| `flowType: sp` | `sp` |
| `flowType: hc` | `hc` |
| `flowType: inv` | `inv` |
| `flowType: scm` | `scm` |
| `flowType: batch` | `batch` |
| `flowType: api` | `api` |
| `flowType: cpy` | `cpy` |
| `flowType: sftp` | `sftp` |
| `flowType: cal` | `cal` |
| `flowType: trl` | `trl` |

Global run options that apply across kinds: `--show-sql` prints the generated SQL to the console after any run; `--log-level info|debug|trace` sets the `run.log` detail for ing/exp/sp/hc runs (trace weaves every statement into the timeline); `--json` outputs the run result as JSON. `--dry-run` and `--no-push` apply to scm runs.

## The file flow: the default document shape

With no `flowType` key, the document is a file flow: files into SQL Server. Its top-level keys map to `FlowYaml` (src/SqlFlow.Yaml/FlowYaml.cs) and validate into a `FlowDefinition` (src/SqlFlow.Core/Model/FlowDefinition.cs).

```yaml
name: orders

source:
  type: csv
  location: ./orders.csv
  options:
    delimiter: ","
    header: true

target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders

schema:
  evolve: widen
  defaultColumnType: varchar(255)

load:
  mode: append
  batchSize: 50000
```

| Key | Type | Required | Description |
| --- | --- | --- | --- |
| `name` | string | yes | Flow name; seeds the deterministic flow identity |
| `batch` | string | no | Grouping label; blank collapses to null and the flow reports under the catalog's default batch |
| `lifecycle` | string | no | `production` (default) or `development`; a development flow runs normally but never generates notification events |
| `source` | map | yes | `type`, `location`, `options`; see the source reference |
| `target` | map | yes | `connection`, `schema`, `table` (each required) |
| `schema` | map | no | Evolution policy, default column type, per-column overrides |
| `load` | map | no | `mode`, `batchSize`, `tableLock`, `manageIndexes` |
| `transform` | map | no | Type inference and per-column transforms |
| `preProcess` | list of SQL strings | no | Run on the target before the load; defaults to an empty list |
| `postProcess` | list of SQL strings | no | Run on the target after the load; defaults to an empty list |
| `desiredIndexes` | string | no | `CREATE INDEX` statements applied when the table is created; blank becomes null |
| `incremental` | map | no | File-date or row-level watermark incremental loading |
| `schedule` | map | no | The shared schedule envelope |

Missing required keys fail with `<file>: 'name' is required.`, `<file>: 'source' is required.`, `<file>: 'target' is required.`, and within the maps `'source.type'`, `'target.connection'`, `'target.schema'`, `'target.table'` follow the same `'<key>' is required.` pattern.

`source.type` selects the reader; the registered readers accept (case-insensitive) `csv`, `xls`, `xlsx`, `json`, `jsonl`, `ndjson`, `xml`, `parquet`, `prq`, `duckdb` (a DuckDB query over Parquet/CSV/JSON locations), and `delta` (a Delta table read through DuckDB's `delta_scan`).

Two identity behaviors worth knowing:

- `name` derives `FlowDefinition.FlowId`, a deterministic GUID computed from the name. It is not authored in YAML, it is identical on every execution, and renaming the flow yields a new identity by design. Logs, lineage, and run history join on it.
- The loader injects `options["flowId"]` (the derived GUID string) into `source.options`, so metadata records carry the identity too.

## The flowType kinds, at a glance

Each kind has its own document shape and its own reference page; the snippets below show the minimal discriminating shape, adapted from samples/.

### flowType: ing

```yaml
flowType: ing
name: orders-ingestion
connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}
source:
  server: erp
  object: AdventureWorks.Sales.Orders
target:
  server: dwh
  object: DW.raw.Orders
load:
  keyColumns: [OrderID]
```

Full sample: samples/ingestion/orders-ingestion.flow.yaml.

### flowType: exp

```yaml
flowType: exp
name: orders-export
connections:
  dwh: ${env:SQLFLOW_DW}
source:
  server: dwh
  object: DW.raw.Orders
target:
  path: ./out
  fileName: orders
```

The target `path` resolves relative to the document. Full sample: samples/export/orders-export.flow.yaml.

### flowType: sp

```yaml
flowType: sp
name: refresh-marts
connections:
  dwh: ${env:SQLFLOW_DW}
procedure:
  server: dwh
  object: DW.dbo.usp_RefreshMarts
```

Full sample: samples/sp/refresh-marts.flow.yaml.

### flowType: inv

```yaml
flowType: inv
name: trigger-refresh
servicePrincipals:
  deploy:
    tenantId: 00000000-0000-0000-0000-000000000000
    clientId: 00000000-0000-0000-0000-000000000000
    clientSecret: ${env:SQLFLOW_DEPLOY_SP_SECRET}
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod
invoke:
  type: adf
  pipeline: pl_refresh_marts
  servicePrincipal: deploy
```

The same `invokes:` and `servicePrincipals:` blocks work inside ing/exp/sp documents as pre/post hooks. Full sample: samples/invoke/trigger-refresh.flow.yaml.

### flowType: hc

```yaml
flowType: hc
name: orders-watch
connections:
  dwh: ${env:SQLFLOW_DW}
target:
  server: dwh
  object: DW.dbo.Orders
dateColumn: OrderDate
metrics:
  - name: orders
    baseValue: COUNT(*)
```

Full sample: samples/healthcheck/orders-healthcheck.flow.yaml.

### flowType: scm

```yaml
flowType: scm
name: dw-schema-history
batch: schema-history
connections:
  dwh: ${env:SQLFLOW_DW}
source:
  server: dwh
repository:
  path: ./dw-schema
  remote: https://github.com/example/dw-schema.git
  branch: main
  secret: ${env:SQLFLOW_GIT_TOKEN}
schedule:
  cron: "0 3 * * *"
  timezone: Europe/Oslo
```

Scripts the database's objects with SMO into the git working tree at `repository.path` (one folder per object type, the legacy layout), commits, and pushes over HTTPS. A snapshot is a maintenance flow: it is a full catalog pipeline (it schedules on the existing scheduler and keeps run history like any other flow) but it is excluded from the lineage graph, because it moves no data between catalog objects. `repository.secret` and `repository.username` must be whole `${env:...}` or `${keyvault:...}` references; a literal fails validation with `'<field>' must be a ${env:NAME} or ${keyvault:vault/secret} reference, never a literal.` Setting `repository.remote` without `repository.secret` also fails at parse time (pushing needs a credential: a BitBucket app password or a GitHub token). On `run`, `--dry-run` scripts and writes the tree without committing; `--no-push` commits locally only. The document's `name` is required; a missing one fails with `'name' is required for a source-control flow (flowType: scm).`

### flowType: batch

```yaml
flowType: batch
name: seed-batch
members:
  include:
    - orders-export.flow.yaml
```

Member paths are globs relative to the batch document's directory. Lineage computes concurrency waves over the members, each wave runs concurrently, and the whole wave finishes before the next starts. A batch cannot be a member of another batch; attempting it fails the member with `a batch cannot be a member of another batch.` Full sample: samples/seed/seed-batch.flow.yaml.

### flowType: trl

```yaml
flowType: trl
name: osdu-dataset-translate
source:
  connection: ${env:SQLFLOW_DW}
  query: SELECT DatasetId, FileName FROM DW.edw.DatasetFile
template:
  id: "kolumbus:dataset--File.Generic:{DatasetId}"
  kind: osdu:wks:dataset--File.Generic:1.1.0
  data:
    Name: "{FileName}"
output:
  path: ./out/osdu/dataset
```

One document per query row (or one per result set), shaped by the template: plain keys are object properties, sequences are arrays, `"{Column}"` substitutes a row value (a whole-string token keeps the column's native JSON type), and `$`-prefixed directives cover typing, null handling, and `$forEach` arrays over bound child datasets. The documents are always saved first (deterministic overwrite), and an optional `invoke:` block then delivers the saved files to an HTTP API through the same auth/reliability surface as an api flow. Full sample: samples/translate/osdu-dataset-translate.flow.yaml. Reference: [Translate flows (flowType: trl)](./trl.md).

## Working with any document kind

```bash
# Validate any kind; the OK line names the kind it parsed
sqlflow validate samples/quickstart/orders.flow.yaml
# OK  'orders' is valid (source: csv, target: [dbo].[Orders]).

sqlflow validate samples/ingestion/orders-ingestion.flow.yaml
```

```bash
# Run any kind through the same verb; plan is file-flow only
sqlflow run samples/export/orders-export.flow.yaml --json
sqlflow plan samples/quickstart/orders.flow.yaml
```

## See also

- [File flow source section](./source.md)
- [Ingestion flows (flowType: ing)](./ing.md)
- [Batch flows (flowType: batch)](./batch.md)
- [Translate flows (flowType: trl)](./trl.md)
- [Flow identity](../concepts/flow-identity.md)
- [sqlflow validate](../cli/validate.md)
