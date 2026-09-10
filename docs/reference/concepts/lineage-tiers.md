---
id: concept-lineage-tiers
title: "Lineage tiers: declared, observed, and derived (SQL module expansion)"
type: concept
summary: "The three lineage provenance tiers: declared YAML intent, observed run.json execution, and derived live-catalog expansion via sys.sql_modules."
keywords:
  - declared
  - observed
  - derived
  - scriptdom
  - sys.sql_modules
  - module expansion
  - run artifacts
  - provenance
  - connectivity posture
related:
  - cli-lineage
  - concept-lineage-graph-and-plan
  - guide-lineage-demo
  - concept-run-artifacts
sourceRefs:
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Lineage/LineageService.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/RunArtifactCollector.cs
  - src/SqlFlow.Lineage/Collection/CatalogCollector.cs
  - src/SqlFlow.Lineage/Collection/ScriptFactBuilder.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Lineage/Extraction/TSqlLineageExtractor.cs
  - src/SqlFlow.Lineage/Extraction/ScriptDependencies.cs
  - src/SqlFlow.Core/Runs/RunHistoryWriter.cs
  - src/SqlFlow.Cli/Program.cs
---

# Lineage tiers: declared, observed, and derived (SQL module expansion)

Every lineage fact in SQLFlow carries its provenance. The `LineageTier` enum (src/SqlFlow.Core/Lineage/LineageReport.cs) defines three tiers:

| Tier | Value | Source of truth | Availability |
| --- | --- | --- | --- |
| `Declared` | 0 | The flow documents (YAML): what the author intends | Always; nothing but the files needed |
| `Observed` | 1 | The latest `run.json` artifact per flow (its `sqlTrace`): what actually executed, stamped with the run | Offline |
| `Derived` | 2 | Live catalog metadata plus `sys.sql_modules` definitions parsed as T-SQL: what the database adds | Needs connectivity |

The tiers exist so the graph is honest in every connectivity posture. Each `LineageEdge` records its `Tier`, and the report's `TiersUsed` lists which tiers fed the computation, so a consumer can always tell declared intent from observed execution from database-derived expansion. The tiers also cross-check each other: a declared read that never shows up observed, or an observed write the YAML never declared, is visible as a tier mismatch rather than silently merged away.

Observed edges additionally carry `ObservedRunId`, `ObservedAtUtc`, and the trace `Step` that produced the statement. Derived edges attributed through a module carry `ViaModule`, the node key of the view or procedure whose definition produced the fact.

Tier selection lives in `LineageOptions` (src/SqlFlow.Lineage/LineageService.cs): `IncludeObserved` defaults to `true` (the CLI flag `--no-observed` disables it) and `IncludeDerived` defaults to `false` (the CLI flag `--connect` enables it). The declared tier always runs. The offline tiers must stand on their own; the derived tier is an enrichment, never a prerequisite.

## Declared: what each flow kind contributes from its YAML

`FlowSetCollector` (src/SqlFlow.Lineage/Collection/FlowSetCollector.cs) scans the folder recursively for `*.flow.yaml`, ordinal-sorted. A document that fails to parse becomes the warning `<file>: skipped: <message>` and the scan continues; one broken file never blinds the estate.

Each flow kind contributes fixed facts:

| Kind | Facts |
| --- | --- |
| `ing` (ingestion) | Flow name is `sysAlias` when set, otherwise the target table name. `Reads` the source table, `Writes` the target table (kind hint Table). When the transform generates a view, an extra `Writes` fact for `v_<TargetTable>` (kind View). |
| `file` | `Reads` the file endpoint from `source.location`, `Writes` `target.schema`.`target.table`. When inference generates a view, an extra `Writes` fact for `v_<Table>` in the target schema. |
| `exp` (export) | `Reads` the source object, `Writes` the file endpoint at `trgPath`. |
| `sp` (stored procedure) | `Requires` the procedure only; the body's reads and writes are the procedure module's lineage, expanded by the derived tier. |
| `hc` (health check) | `Reads` the target object. |
| `inv` (invoke) | A lineage node with no facts; it triggers external compute and moves no catalog data. |
| `scm` | Nothing. A source-control snapshot is a schedulable pipeline but a maintenance one: it scripts object definitions to a git tree, so it declares no data dependency and is excluded from the graph entirely (no node, no edge, no wave). |
| `batch` | Nothing. A batch's ordering is computed FROM lineage, never part of it. |

The generated-view fact is what connects a landing flow to its downstream readers: the landing flow declares it writes the view, the downstream ingestion flow declares it reads the view, and the dependency resolves entirely at the declared tier.

Ingestion (`ing`) flows' `preProcess`/`postProcess` hooks are raw author T-SQL: they run through the same AST extractor as everything else, and their facts attribute to the flow as Declared lineage (minimum two-part threshold relaxed to one part, target-side server, target database as the default database). File flows accept `preProcess`/`postProcess` hooks too (as YAML lists rather than single strings), but `FlowSetCollector` does not extract them into lineage facts.

Two collector warnings guard estate hygiene:

- `flow name '<X>' is declared by N documents (<files>); their facts merge under one flow, which is almost never intended.` The graph collapses the collision to the first declaration and also exposes it structurally in the report's `DuplicateFlowNames`.
- `server '<X>' is declared with conflicting providers (<A> vs <B>); the first wins.`

File endpoints are identity-normalized: cloud URLs (`://`) verbatim, local paths resolved against the estate root and kept relative when inside it, so `./data/x.csv` and `data/x.csv` are one node and the identity survives a checkout moving between machines.

## Observed: the latest run.json of each flow

`RunArtifactCollector` (src/SqlFlow.Lineage/Collection/RunArtifactCollector.cs) reads each flow's LATEST canonical run artifact only. Run history lives at `<document directory>/.sqlflow/runs/<SafeName(flow)>/` (every document directory is scanned, plus the estate root's `.sqlflow/runs`); run folders are timestamp-prefixed, so the lexicographic maximum folder is the latest run. `RunHistoryWriter.SafeName` (src/SqlFlow.Core/Runs/RunHistoryWriter.cs) maps invalid filename characters to `_`; it is the one naming rule for every per-flow folder under `.sqlflow`.

From `run.json`:

- Statements come from `result.sqlTrace` entries. An entry whose `step` starts with `source.` is attributed to the flow's source server; everything else to the target server.
- File flows contribute `result.ddlExecuted` (a plain string list) to the target side.
- Each side's statements are joined with `GO` into ONE script before extraction, so the engine's transient staging tables (created, loaded, read, then dropped within the run) dissolve through the extractor's local-dependency resolution instead of polluting the graph.
- Observed identities require at least `schema.name` (minimum two parts; the engine's generated SQL is fully qualified).
- Every fact is stamped with the artifact's `runId` and `writtenUtc`, and `Step` is `trace/source` or `trace/target`.

Degradation is loud, never fatal:

- A flow document newer than its last run warns `<flow>: the flow document changed after its last run (<time>Z); observed lineage may be stale until the next run.`
- A run folder with no matching flow document warns `run history '<folder>' has no matching flow document; its observations are not attributed (a renamed or deleted flow).`
- A corrupt or unreadable artifact warns `<flow>: run artifact unreadable (...); observed lineage skipped for this flow.` and the pass continues.

## Derived: connected catalog inventory and module harvest

`CatalogCollector` (src/SqlFlow.Lineage/Collection/CatalogCollector.cs) runs only with `--connect`. It connects to the SQL Server references the documents declared (`DataSourceKind` `MSSQL` or `AZDB`); any other provider kind warns `server '<X>' is <kind>; module-level lineage derivation covers SQL Server only.` (the `file` identity is exempt). Servers are processed in parallel, and an unreachable server degrades to `server '<X>': derived lineage unavailable (<redacted message>); the offline tiers still apply.`

Before connecting, an identity-proof pass resolves every reference to its canonical connection string; references that resolve to equal strings register as `ServerAliases`, so one physical server referenced under several spellings becomes one node space in the graph.

Per reachable server, four read-only passes (all with `CommandTimeout = 0`):

1. **Inventory**: `sys.objects` joined to `sys.schemas`, types `U`/`V`/`P`/`FN`/`IF`/`TF`/`TR` with `is_ms_shipped = 0`, mapped to Table, View, Procedure, Function, and Trigger nodes. Columns for `U`/`V`/`IF`/`TF` objects come from `sys.columns` joined to `sys.types` and render as DDL-style type strings: `nvarchar`/`nchar` halve `max_length` (or `max`), `varchar`/`char`/`varbinary`/`binary` take the length, `decimal`/`numeric` take `(precision,scale)`, `datetime2`/`datetimeoffset`/`time` take `(scale)`.
2. **Synonyms**: `sys.synonyms` with `PARSENAME` splitting the base object name server-side. A four-part (linked server) base is surfaced as the node warning `synonym base lives on linked server '<X>'; not resolvable from here.` instead of being guessed. Resolvable synonym links are applied by the graph builder so every fact lands on the base object.
3. **Module harvest**: `sys.sql_modules` bodies are read verbatim and parsed with the shared T-SQL extractor. The resulting facts are module-attributed Derived facts (`ViaModule` = the module's node key); the module's own `CREATE` statement points at itself and such self-facts are dropped. The definition is retained on the object node, so the connected catalog is searchable code. An encrypted module (`NULL` definition, `WITH ENCRYPTION`) becomes the node warning `module is encrypted (WITH ENCRYPTION); its definition cannot be read, so its lineage is unknown.`
4. **Table scripts**: for every base table (`sys.columns`/`sys.types`/`sys.identity_columns`/`sys.computed_columns` for the columns plus `sys.indexes`/`sys.key_constraints` for the primary key), a `CREATE TABLE` script is reconstructed from the live schema and attached to the object node as a derived-tier `Script` artifact. SQL Server keeps no CREATE TABLE text the way it keeps a module's `sys.sql_modules` body, so without this pass the catalog would hold a generating script for views/procedures but never for tables; the reconstructed script is what lets the estate be recreated elsewhere and reasoned about offline.

Each connection also records `SELECT DB_NAME()` as that server's default catalog: node-identity ground truth used to complete database-less (two-part) identities against the exact catalog the engine executes them in. After all tiers have collected, a default-database resolution pass fills the remaining servers offline from each reference's `Initial Catalog`.

## Module expansion: view chains and procedure bodies in flow lineage

`LineageGraphBuilder` (src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs) folds module-attributed derived facts back into flows in two distinct ways:

- **Scheduling inheritance** (`InheritModuleRelations`): a flow that `Reads` a view or `Requires` a procedure inherits the module's derived relations, transitively through module-on-module references. This is what connects a flow reading `dbo.vw_Orders` to the flow loading `dbo.Orders`. The inherited relations feed the scheduling relations, so view chains and procedure bodies participate in wave ordering.
- **Flow-attributed edges** (`InheritedProcedureEdges`): a flow that EXECUTES a procedure (its own `Requires` of a module) additionally gets flow-attributed Derived edges for the procedure's transitive `Reads`/`Writes`/`Creates`, each with the owning module as `ViaModule` provenance. A table a stored-procedure flow builds therefore traces back to the FLOW that runs it, not only to the procedure module. The walk follows the procedure's own `Reads`/`Requires` into further modules (a procedure reading a view, or calling another procedure), so a table two hops down still traces to the executing flow.

Both walks are depth-guarded at `MaxModuleDepth = 32`. Exceeding it in scheduling inheritance warns `flow '<name>': module expansion beyond 32 levels at '<module>'; deeper lineage is not inherited.` A deeper chain is treated as a modeling error, not lineage.

## The shared T-SQL extractor

All three tiers feed raw T-SQL (hooks, traces, module bodies) through one extractor, `TSqlLineageExtractor` (src/SqlFlow.Lineage/Extraction/TSqlLineageExtractor.cs):

- Parses with `TSql160Parser` (quoted identifiers on) and destructures ScriptDom's typed AST statement by statement. There is deliberately NO flat fragment visitor: each statement kind has an explicit handler that knows which positions write and which read, so MERGE targets, UPDATE aliases, and CTEs are position-aware.
- Statement kinds without a handler become warnings carrying their type name, never silent gaps; parse errors warn `<label>: parse error at line N: <message>`.
- The recursion ceiling is `MaxDepth = 64`. CTE scoping, subquery depth, USE-database tracking, and table variables are tracked per script with no shared state.
- Typed relations classify outbound objects by LIFECYCLE over the combined operation set (src/SqlFlow.Lineage/Extraction/ScriptDependencies.cs): drop+create+data = `Writes` (a refresh); create = `Creates` (plus `Writes` when data operations exist); drop alone = `Destroys` (even if the script also inserted); data or truncate = `Writes`; alter alone = `Creates`. Every `Writes` without a `Creates` derives a `Requires` prerequisite.
- Inbound: self-referential reads (read after own data write) are pruned; data reads become `Reads` and structural references become `Requires`; script-created entities that were also read still emit `Reads` so internal chains stay visible.
- Run-scoped staging (created and LATER dropped, order-sensitive `CreatedThenDropped`) never reaches the graph, while drop-then-create rebuilds keep their relations. Temp tables (`#`) and identities below the caller's part threshold stay out.
- A `defaultDatabase` qualifies one- and two-part names when the script's home database is known; an explicit database part or a `USE` statement always wins.

`ScriptFactBuilder` (src/SqlFlow.Lineage/Collection/ScriptFactBuilder.cs) is the single mapping from extracted dependencies to lineage facts, shared by document hooks (minimum one part), run traces (minimum two parts), and harvested modules (minimum one part), so the relation semantics are identical everywhere.

## Configuration touchpoints

- `sqlflow lineage <folder>` computes offline lineage (declared + observed) by default. `--connect` adds the derived tier; `--no-observed` drops the observed tier. `--strict` exits 2 when the report contains cycles; otherwise the command exits 0 on a computed report.
- The canonical artifact is `<folder>/.sqlflow/lineage/lineage.json`; `--dump-facts` writes the raw pre-merge facts (what each tier contributed, per fact with its tier) to `<folder>/.sqlflow/lineage/facts.json`.
- `--of <object|flow>` walks impact (`--down`, the default) or dependencies (`--up`); `--explain <flow>` traces why a flow sits in its wave, with per-edge tier provenance.
- `sqlflow db sync <path> --connect` applies the same derived tier when projecting the estate into the shadow catalog.
- Server identity follows connection references: two documents referencing `${env:SQLFLOW_CONN_DWH}` are one server by construction; inline connection-string literals are identified by a content hash and never echoed (an inline literal shows as `(inline literal redacted)` in the facts dump). Secret resolution defaults to environment variables when `LineageOptions.Secrets` is null.

## Example

The seven-flow estate in samples/lineage-demo lands a five-wave plan across the tiers. Offline, the declared view facts alone order the landing-to-ingestion chain:

```bash
# Declared + observed (offline): land -> view -> ing dependencies resolve from YAML alone.
sqlflow lineage samples/lineage-demo --dump-facts

# Add the derived tier: procedure bodies from sys.sql_modules order ing -> sp -> sp.
sqlflow lineage samples/lineage-demo --connect --explain demo-build-country-rollup
```

The landing flow declares the view it refreshes; this single Declared fact is what connects it to the downstream ingestion flow:

```yaml
# samples/lineage-demo/10-land-orders.flow.yaml
name: demo-land-orders
source:
  type: csv
  location: data/orders.csv
target:
  connection: ${env:SQLFLOW_DEMO_DB}
  schema: demo
  table: Orders_Pre
transform:
  inferTypes: true
  columns:
    - name: vehicle_type
      expr: "UPPER(CAST(@ColName AS varchar(50)))"
      as: vehicle_type_clean
      type: varchar(50)
```

Declared facts for this flow: `Reads` the file `data/orders.csv`, `Writes` `demo.Orders_Pre` (Table), and `Writes` `demo.v_Orders_Pre` (View). After a run, the observed tier re-derives the same graph from the executed SQL, stamped with the run id; with `--connect`, the derived tier expands `demo.usp_BuildOrderFact` and the rollup procedures so the downstream `sp` flows trace through their procedure bodies to `demo.Fact_OrderSummary` and beyond.

## Reaching the relationships

The interpreted relationships are served on the object dossier (`GET /api/v1/lineage/objects/dossier`) and, narrowed to one answer, through the `get_table_joins` and `get_table_key` MCP tools. See [Data operations](data-operations.md) for how a model is meant to use them when authoring SQL.

## See also

- [sqlflow lineage](../cli/lineage.md): the command, its flags, and output shapes
- [Lineage graph and execution plan](./lineage-graph-and-plan.md): how facts merge into waves, dependencies, and cycles
- [Lineage demo walkthrough](../../guides/lineage-demo.md): the seven-flow estate end to end
- [Run artifacts](./run-artifacts.md): the run.json contract the observed tier reads
