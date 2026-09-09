---
id: concept-shadow-catalog
title: "The shadow catalog: sync mechanics, projections, and identities"
type: concept
summary: How the catalog database mirrors the git/YAML estate and run history one way, with SHA-256 change detection, redaction, and deterministic ids.
keywords:
  - shadow catalog
  - read-model
  - sha-256 hashing
  - redaction
  - run projection
  - pipeline columns
  - full-text search
  - repo-scoped ids
related:
  - cli-db
  - cli-run
  - concept-control-plane
  - concept-lineage-graph-and-plan
sourceRefs:
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogProjection.cs
  - src/SqlFlow.Catalog/CatalogIdentity.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogDbContext.cs
  - src/SqlFlow.Catalog/Migrations/20260617165538_ObjectFullTextSearch.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Cli/Program.cs
---

# The shadow catalog: sync mechanics, projections, and identities

The shadow catalog is a SQL Server read-model of the SQLFlow estate. Git/YAML flow documents plus the on-disk `run.json` history are the source of truth; the catalog database mirrors them in ONE direction (files to database, never back), so it can always be rebuilt from scratch by re-syncing. Several git repos sync into a single catalog for cross-repo queries; every pipeline and run row is attributed to its repo. The whole schema lives under the `catalog` schema and is owned by EF Core migrations (`CatalogDbContext`, `src/SqlFlow.Catalog/CatalogDbContext.cs`); it is the only place in the product that uses Entity Framework.

Tables in the `catalog` schema: `Repo`, `Pipeline`, `Run`, `Object`, `LineageEdge`, `FlowDependency`, `RunFile`, `RunAssertion`, `RunStatement`, `RunEvent`, `RunSurrogateKey`, `RunHealthCheckMetric`, `ObjectColumn`, `PipelineColumn`, `Schedule`, `Node`, `RepoSource`, `User`, `Role`.

A full sync pass (`CatalogSync.SyncAsync`, `src/SqlFlow.Catalog/CatalogSync.cs`) runs in one serializable transaction wrapped in the context's execution strategy, so two concurrent syncs of the same repo serialize instead of racing, and a connection-resiliency retry re-collects the estate from disk safely.

## Deterministic, repo-scoped identities

All ids are computed, never allocated, so no database round-trip is needed to join anything (`src/SqlFlow.Catalog/CatalogIdentity.cs`):

- A repo's id is `FlowIdentity.FromName(repoName)`: re-syncing the same repo name updates its row in place.
- A pipeline's id is `FlowIdentity.FromName($"{repoId:N}/{flowName}")` (`CatalogIdentity.Pipeline`). It is repo-scoped: the same flow name in two repos is two distinct pipelines. A run artifact discovered later computes the same pipeline id the registry sync did, so the two join without a lookup.
- A YAML-declared schedule's id is `FlowIdentity.FromName($"{repoId:N}/{flowName}/schedule")` (`CatalogIdentity.YamlSchedule`): one per flow, updated in place on re-sync. API-created schedules get fresh ids instead.

Pipeline names are unique per `(RepoId, Name)`, not globally. Cross-table links (`Run.PipelineId`, `PipelineColumn.PipelineId`, `Schedule.PipelineId`, `LineageEdge.PipelineId`) are soft links with no foreign keys, so run history and columns survive a pipeline row leaving the estate. The `(RepoId, Wave)` index lists a repo's pipelines in execution order.

## Schema history

Source-control (`scm`) runs additionally project into `SchemaChange`: one row per database object they found
added, changed, or dropped, carrying the database, the object category, schema and name, the change kind, the
commit that holds the diff, and when the snapshot observed it. It is written by the same one-run-one-insert path
as every other run detail (`CatalogSync.AddRunDetail`), so the CLI, the full sync, and the live run queue record
it identically. Non-scm runs project no rows, and a dry run is deliberately skipped.

The rows are keyed by their own `RepoId` and outlive nothing else: they are not pruned with run history, because
a change feed that forgets is not a history. They are removed only when their repo is deleted.

## Pipeline sync: hashing, redaction, soft deactivation

Each present flow document is projected into a `CatalogPipeline` row. The hot dimensions are columns: `Kind` (`file` / `ing` / `api` / `cpy` / `sftp` / `exp` / `trl` / `sp` / `inv` / `hc` / `cal` / `scm`), `Batch`, `RelativePath` (forward-slashed), `SourceServer`, `TargetServer`, and `Wave`. Batch-orchestration (`batch`) documents declare no flow of their own, so they never get a `CatalogPipeline` row; a run recorded from one still lands in `Run` (artifact discovery accepts any `flowKind`), just with a `PipelineId` that never resolves to a pipeline row. Source-control (`scm`) documents DO get a pipeline row (so a snapshot schedules and runs like any other flow) but are excluded from the lineage graph, so their `Wave` stays at the `-1` "not computed" sentinel; see [Lineage graph and plan](lineage-graph-and-plan.md). The full definition is stored twice:

- `Yaml`: the original document text, for display and search.
- `DefinitionJson`: the parsed flow serialized to camelCase JSON (with `JsonStringEnumConverter` and null-value omission), so any field is queryable via `JSON_VALUE` / `OPENJSON` across every file and repo. A document that fails to parse stores an empty definition with the warning `'<path>' could not be parsed for the catalog definition (...); stored without it.`

`Batch` keeps an honest null when the YAML declares none; grouping surfaces coalesce to `CatalogPipeline.DefaultBatch` (`"default"`) at query time, never in the row.

Secrets never rest in the catalog. If the raw YAML looks like it embeds a credential, the sync warns: `'<flow>' (<file>) appears to embed a credential; it is redacted in the catalog, but secrets must be ${env:...}/${keyvault:...} references in the YAML, not literals.` The text is then passed through `SecretHygiene.RedactedMessage` BEFORE it is stored or hashed, so change detection runs on the safe form. The same redactor is applied to `DefinitionJson` and to captured module bodies.

Change detection is a lowercase-hex SHA-256 of the redacted YAML (`CatalogProjection.Hash`, stored in `ContentHash`, nvarchar(64)). An unchanged hash only re-affirms `Active`, `LastSeenUtc`, and `RelativePath`; a changed hash rewrites the whole row.

Edge handling during a pass:

- A duplicate flow name within a repo keeps the first document (the estate scan already warned about the duplicate).
- A file that vanished or became unreadable between scan and read leaves the existing row untouched (it stays "present" and is not deactivated) instead of overwriting it with empty content.
- Flow YAML over 16 MiB (`MaxYamlBytes = 16 * 1024 * 1024`) is skipped with a warning; `run.json` over 64 MiB (`MaxRunJsonBytes = 64 * 1024 * 1024`) likewise.
- A flow that left git is soft-deactivated (`Active = false`, counted as `PipelinesDeactivated`) so its run history stays attributable.

The pass returns a `CatalogSyncResult` tally: `PipelinesAdded` / `Updated` / `Unchanged` / `Deactivated`, `RunsAdded` / `Skipped` / `Failed`, `ObjectsUpserted`, `ObjectsSuperseded`, `ObjectColumns`, `LineageEdges`, `FlowDependencies`, `Waves`, `RunFilesAdded`, `RunAssertionsAdded`, `RunStatementsAdded`, `RunEventsAdded`, `RunSurrogateKeysAdded`, `RunHealthCheckMetricsAdded`, `LineageConnected`, `Warnings`.

## Run history projection

Run artifacts are discovered as every `run.json` under any `/.sqlflow/runs/` subtree of the synced folder (case-insensitive path match). A valid header requires `runId` (a GUID string), `flowName`, and `flowKind`; anything else is skipped with `run artifact '<file>' is missing required fields; skipped.` All property lookups are case-insensitive.

Runs are insert-only, keyed by their own `RunId`, so re-syncing the same folders (or aggregating many nodes' folders) is idempotent: already-known ids count as `RunsSkipped`, unreadable or oversized files as `RunsFailed` with a redacted warning. `CatalogRun.PipelineId` is the same repo-scoped identity the pipeline sync computes (`CatalogIdentity.Pipeline(repoId, flowName)`), a soft link with no FK, so runs outlive removed pipelines.

A run recorded from its artifact is born in a terminal `Status`: `succeeded` or `failed` from the artifact's `success` boolean. Queue-born runs (control plane) use the lifecycle `queued` -> `running` -> `succeeded` / `failed`, or `cancelled` while still queued (`RunStatuses` constants with an `IsTerminal` helper).

Metric fields are best-effort from the kind-specific `result`: `StartUtc` / `EndUtc` from `result.startTimeUtc` / `endTimeUtc`, `DurationSeconds` from `result.durationSeconds` or `result.totalMs / 1000`, `RowsLoaded` from `result.rowsLoaded ?? result.totalRows`, plus `RowsInserted` / `RowsUpdated` / `RowsDeleted`; `Host` comes from the root's `host` field.

Per-run substitution columns make the built-in backfill auditable from history: `FullLoad`, `BackfillFrom` / `BackfillTo` (UTC), `FilePattern`. Queue-routing columns are `TargetPool` (null means any node), `CommitSha` (a pinned commit), `ClaimedByNode`, and `EnqueuedUtc`. The control plane's run queue sets these eight when it enqueues and claims a run (`RunQueueStore`, `src/SqlFlow.Catalog/RunQueueStore.cs`), not the run.json projection: a run recorded straight from an on-disk artifact leaves them at their defaults, since `run.json` carries none of them.

### Drill-down tables

A run is immutable, so its detail rows are inserted exactly once, with the run itself (`CatalogSync.AddRunDetail`):

| Table | Source in run.json | Notes |
| --- | --- | --- |
| `RunFile` | `result.processedFiles` (name, path, rows, columns, sizeBytes) and export `result.files` (path, rows, bytes) | Export file name is the path's last segment; export rows carry no column count |
| `RunAssertion` | `result.assertions` | Name, result, asserted value, evaluated flag, error |
| `RunStatement` | `result.sqlTrace` plus a file flow's `result.ddlExecuted` (step `schema.ddl`) | 1-based ordinals in execution order; surrogate-key SQL is already inside `sqlTrace` and deliberately not read twice |
| `RunEvent` | the top-level `events` array (`timestampUtc`, `level`, `step`, `message`, `rows`, `elapsedMs`) | 1-based ordinals in execution order; an entry missing a message or timestamp is skipped |
| `RunSurrogateKey` | `result.surrogateKeys` | Keys generated, rows stamped, remote flag, error |
| `RunHealthCheckMetric` | `result.metricResults` | Series/imputed/immature points, anomalies, level shifts, model provenance |

`CatalogRunStatement` is the V3 equivalent of the legacy `flw.SysLog.TraceLog`: every statement the engine generated for a run, queryable from the database instead of the file. `Step` is truncated to 128 characters; `Sql` is nvarchar(max).

`RunStatement` and `RunEvent` are special: a queue-run's node streams them into the catalog live as it executes (`CatalogRunStatementSink` / `CatalogRunEventSink`), and the trace SSE stream tails those rows by their id. They are therefore an immutable, append-only log: completion does NOT delete and re-project them (that would re-issue every row under a fresh id and make the tail re-stream the whole trace). It reads the highest live ordinal already present and appends only the tail the feed did not write, which is nothing in the normal case and just the gap after a best-effort feed broke. The live-sink ordinals (publication / execution order) match the artifact projection's ordinals, so the append aligns. The CLI and full-sync paths have no live rows, so they insert the whole detail. The other four detail tables are only ever written at completion, so they are still inserted exactly once.

## Lineage projection: global objects, repo-scoped edges, waves

Lineage is an enrichment: a failure to compute it never fails the sync; pipelines and runs still land. On failure, every active pipeline of the repo has its `Wave` reset to `-1` ("not computed") with the warning `lineage was not computed for this sync (...); objects and edges left unchanged, waves reset to not-computed.`

- `Object` rows are GLOBAL, not repo-scoped: keyed by the canonical node key (server reference, database, schema, name), upserted and never deleted, so the same physical object referenced from several repos is one row and "what touches dbo.Customer" joins across repos on the key. `Object.Definition` stores the redacted `sys.sql_modules` body for views/procs/functions/triggers; an offline sync never nulls a stored definition.
- `LineageEdge` rows are repo-scoped and replaced wholesale per sync (delete by `RepoId`, re-insert), so a removed flow's edges never linger. Each carries `Flow`, `PipelineId`, `ViaModule`, `Relation` (Reads / Writes / Creates / Requires / Destroys), `ObjectKey`, a denormalized `ObjectName`, and `Tier` (Declared / Observed / Derived).
- `ObjectColumn` (the cross-repo data dictionary) is populated only by a `--connect` sync. Only objects the derived tier actually re-read this pass (nodes carrying columns) are replaced by key, so an offline sync never wipes a previously collected dictionary.
- Identity healing: when a key gains its database or schema, the weaker twin key from an earlier sync (the same object under a partial key) is deleted, together with its `ObjectColumn` rows, once no repo's edges reference it. Counted as `ObjectsSuperseded`.
- Each pipeline is stamped with its execution `Wave` from the lineage report (`-1` means not computed). `FlowDependency` rows (`FromFlow` / `ToFlow` / `FromPipelineId` / `ToPipelineId` / `ViaObjects` comma-joined) are replaced per repo; together with `Pipeline.Wave` they are the executable order a GUI or orchestrator reads.

## Pipeline columns: declared vs detected

`PipelineColumn` is the modernized, central form of the legacy `flw.PreIngestionTransform`: one row per resolved column of a pipeline's transformation view, so the estate answers "which transformations are set or detected on a pipeline". Two provenances share the row shape, distinguished by `Kind` (`PipelineColumnKinds`):

- `declared`: authored in the flow YAML. Rebuilt wholesale per repo on every full sync (delete this repo's declared rows, re-insert from each present flow's transform policy) and per pipeline on every run write-back. Only `file` and `ing` flow kinds carry authored transforms. The projection resolves the `@ColName` placeholder to the bracket-escaped source column reference; a type-only transform stores `Expression = "CAST([col] AS <type>)"`; `Converted` is true when an expression or type is present.
- `detected`: projected from `result.transformView.columns` in run.json (fields `columnName`, `selectExpression`, `dataType`, `converted`), replaced per `(pipeline, detected)` with latest-run-wins by the run's `WrittenUtc`, so an older artifact synced late never regresses the snapshot.

Row shape: `Ordinal` (1-based view position), `ColumnName` (the alias when renamed), `SourceColumn` (null for virtual and for detected rows), `Expression` (nvarchar(4000)), `DataType` (nvarchar(128)), `SortOrder`, `IsVirtual`, `ExcludeFromView`, `Converted`; `RepoId` and `PipelineId` are soft links. The unique index `(PipelineId, Kind, Ordinal)` is the idempotency backstop for the delete-and-reinsert pattern; `ColumnName` is indexed for estate-wide column search.

## Full-text search

Migration `ObjectFullTextSearch` (`src/SqlFlow.Catalog/Migrations/20260617165538_ObjectFullTextSearch.cs`) creates `FULLTEXT CATALOG [CatalogFullText]` plus full-text indexes on `catalog.Object(Definition)` and `catalog.RunStatement(Sql)`, both `WITH CHANGE_TRACKING AUTO`. Everything is guarded by `SERVERPROPERTY('IsFullTextInstalled') = 1`, so instances without the Full-Text feature get a clean no-op; each create is skipped when already present (idempotent) and runs with `suppressTransaction` because full-text DDL cannot execute inside a user transaction.

`CatalogObject.FullTextKey` is a bigint IDENTITY surrogate whose only purpose is the single-column unique key index full-text requires (`Object.Key` at nvarchar(900) is too wide); the application still keys on `Key`. The intended queries: "the proc whose body references dbo.Orders" (against `Object.Definition`) and searching or diffing a run's exact generated SQL (against `RunStatement.Sql`).

```sql
-- The module whose body references dbo.Orders (requires the Full-Text feature).
SELECT o.[Key], o.Name, o.Kind
FROM catalog.[Object] AS o
WHERE CONTAINS(o.[Definition], '"dbo.Orders"');
```

## Configuration touchpoints

The catalog is operated through the `db` command family and the run write-back (`src/SqlFlow.Cli/Program.cs`):

- `sqlflow db migrate [--db <conn-ref>]` creates or upgrades the schema and prints the applied migration level.
- `sqlflow db sync [path] [--db <conn-ref>] [--repo <name>] [--repo-url <url>] [--connect]` projects the estate and run artifacts into the catalog (migrating first). `path` defaults to the current directory; the repo name defaults to the synced folder's name. `--connect` adds the derived lineage tier (live database metadata and `sys.sql_modules`), which is what populates `ObjectColumn` and module bodies.
- `sqlflow db status [--db <conn-ref>]` reports applied vs pending migrations (exit code 2 when migrations are pending).
- The connection is a reference, defaulting to `${env:SQLFLOW_CATALOG_DB}`. Passing a literal connection string with an embedded credential to `--db` triggers a warning because it lands in shell history.
- After a flow run, when `--db` is passed or `SQLFLOW_CATALOG_DB` is set, the CLI automatically records the produced run(s) and their pipeline row(s) into the catalog (the self-maintaining write-back, `CatalogSync.RecordRunAsync`). Opt out with `--no-db-sync`. The write-back is best-effort (a failure warns and never changes the run's exit code) and deliberately does not recompute lineage or waves; those remain `db sync`'s job. The repo name comes from `--repo`, then `SQLFLOW_REPO`, then the flow's folder name.

Environment variables: `SQLFLOW_CATALOG_DB` (the catalog connection), `SQLFLOW_REPO` (the write-back's repo attribution).

## Example

Sync a repo's estate into the catalog with connected lineage, then query definitions across every flow:

```bash
export SQLFLOW_CATALOG_DB="Server=localhost;Database=SqlFlowCatalog;Integrated Security=true;TrustServerCertificate=true"
sqlflow db sync ./flows --repo finance --connect
```

```sql
-- Every active ingestion pipeline's target table, straight from the definition JSON.
SELECT p.Name,
       JSON_VALUE(p.DefinitionJson, '$.document.flow.target.table.schema') + '.'
         + JSON_VALUE(p.DefinitionJson, '$.document.flow.target.table.name') AS TargetTable,
       COALESCE(p.Batch, 'default') AS Batch,
       p.Wave
FROM catalog.Pipeline AS p
WHERE p.Active = 1 AND p.Kind = 'ing'
ORDER BY p.Wave;
```

## See also

- [cli-db](../cli/db.md): the `db migrate|sync|status` command reference.
- [cli-run](../cli/run.md): running flows, which write the run.json artifacts the catalog projects.
- [concept-control-plane](control-plane.md): the queue, scheduler, and repo sources that live in the same catalog schema.
- [concept-lineage-graph-and-plan](lineage-graph-and-plan.md): how the lineage report (objects, edges, waves) is computed before the catalog stores it.
