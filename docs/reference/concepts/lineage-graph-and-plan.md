---
id: concept-lineage-graph-and-plan
title: "The lineage graph: report model, node identity, execution plan, facts dump"
type: concept
summary: How lineage.json is structured, how nodes get their identity, how the execution plan waves are computed, and what facts.json dumps for debugging.
keywords:
  - lineage.json
  - nodekey
  - execution plan
  - kahn waves
  - cycles
  - facts.json
  - flow dependencies
  - node identity
related:
  - cli-lineage
  - flow-batch
  - concept-lineage-tiers
  - concept-shadow-catalog
sourceRefs:
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Core/Lineage/LineageFactsDump.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Lineage/Collection/DefaultDatabaseResolver.cs
  - src/SqlFlow.Lineage/Collection/CatalogCollector.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/LineageService.cs
  - src/SqlFlow.Cli/Program.cs
---

# The lineage graph: report model, node identity, execution plan, facts dump

Lineage aggregates every flow document, run artifact, and (optionally) live catalog module into one graph of flows, objects, and typed relations, then derives an execution plan from it: which flows can run concurrently, and which must wait. The result is a single versioned artifact, `lineage.json`, written under the scanned folder's `.sqlflow/lineage/` directory. The CLI (`sqlflow lineage`) and the end-to-end tests construct through one front door, `LineageService` in src/SqlFlow.Lineage/LineageService.cs, whose only choice is the connectivity posture (which tiers contribute facts, see [lineage tiers](lineage-tiers.md)). Focused algorithm tests such as `LineageGraphTests` instead call `LineageGraphBuilder.Build` directly against a hand-built collection result, bypassing collection entirely.

Everything is deterministic by construction: objects, edges, warnings, and waves are ordinal-sorted, so the same input produces a byte-for-byte identical report. That makes `lineage.json` diffable across runs and safe to commit or compare in CI.

## The lineage.json report model

`LineageReport` (src/SqlFlow.Core/Lineage/LineageReport.cs) is the canonical artifact, with `CurrentSchemaVersion = 2`. Its envelope:

| Field | Contents |
| --- | --- |
| `SchemaVersion` | Contract version, currently 2 (v2 adds the object `Script`/`ScriptTier`; a v1 reader sees the same shape plus new fields it can ignore). |
| `GeneratedAtUtc` | When the computation ran. |
| `FlowDirectory` | The scanned flow folder (full path). |
| `TiersUsed` | Which tiers fed the graph: `Declared`, `Observed`, `Derived`. |
| `Flows` | One `LineageFlowNode` per flow document. |
| `Objects` | One `LineageObjectNode` per catalog or file object, sorted by key. |
| `Edges` | Every attributed relation between a flow or module and an object. |
| `FlowDependencies` | The flow-level dependency edges behind the plan. |
| `ExecutionPlan` | `Waves` plus `Unordered` (cycle members). |
| `Cycles` | Each dependency cycle, in path order, with mediating objects. |
| `DuplicateFlowNames` | Every flow name declared by more than one document. |
| `Warnings` | Graph-scoped findings; never silently dropped. |

### Flow nodes

`LineageFlowNode`: `Name`, `Kind` (the document kind: `file`, `ing`, `exp`, `sp`, `inv`, `hc`), `File` (the document path relative to the scanned folder), and optional `Batch`. Two document kinds move no catalog data and are therefore absent from the graph, for different reasons:

- `batch` declares no flow of its own; a batch's ordering is computed FROM lineage, never part of it, so it projects no header at all (src/SqlFlow.Lineage/Collection/FlowDocumentHeaders.cs).
- `scm` is a real, schedulable pipeline (it holds a schedule and keeps run history) but a maintenance one: it scripts object DEFINITIONS to a git tree, so it declares no data dependency. Its header carries `ParticipatesInLineage = false` and `LineageGraphBuilder` drops it at one gate before any node is built (src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs), so it acquires no node, no edge, no dependency, no wave, and no batch membership. Its run artifacts are recognized by the observed tier (no orphan warning) but contribute nothing.

### Object nodes

`LineageObjectNode`: `Key` (the canonical node identity, below), `ServerRef`, `Database`, `Schema`, `Name` (raw spellings preserved), `Kind` (`Unknown`, `Table`, `View`, `Procedure`, `Function`, `Trigger`, `Synonym`, `File`), `Definition` (the `sys.sql_modules` body for a view/procedure/function/trigger, captured by the derived tier only; null for a plain table, an offline computation, or an encrypted module), `Script`/`ScriptTier` (the v2 addition: the reconstructed `CREATE TABLE` script for a base table, attached by the derived tier's table-scripts pass, with the tier that produced it), `Columns` (derived tier only: `Ordinal` 1-based, `Name`, `DataType` rendered with length and precision such as `nvarchar(100)`, `Nullable`), and node-scoped `Warnings` (an encrypted module whose definition is unreadable, for example).

### Edges and relations

`LineageRelation` is the typed-relation taxonomy:

| Relation | Value | Meaning |
| --- | --- | --- |
| `Reads` | 0 | Consumes the object's data (a table-valued function call included: it is read AND internally marked as requiring the function to exist, but the read wins the classification). |
| `Writes` | 1 | Modifies the object's data. |
| `Creates` | 2 | Brings the object into existence; an `ALTER` with no accompanying create, drop, write, or truncate on the same object is bucketed here too (the object's structural source). |
| `Requires` | 3 | Needs the object to exist: a static EXEC of a procedure (naming it directly, not through a variable or dynamic SQL), or a write/`TRUNCATE` against an object the same script did not create. |
| `Destroys` | 4 | Removes the object. |

A `LineageEdge` carries `Flow` (null for a plain module-derived fact), `ViaModule` (the module key whose definition produced the fact; null for a plain flow-level fact), `Relation`, `ObjectKey`, `Tier`, and observed-tier provenance (`ObservedRunId`, `ObservedAtUtc`, `Step`). Edge identity is the tuple `(Flow, ViaModule, Relation, ObjectKey, Tier)`; duplicate facts dedupe with the latest `ObservedAtUtc` winning (src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs). A flow whose own facts `Require` a module (an sp flow executing its procedure) also gets flow-attributed derived edges for what that procedure, and anything it in turn reads or requires, does: these carry both `Flow` and `ViaModule` together (the module is the specific step in the chain), so a table a stored-procedure flow builds traces back to the flow itself, not only to the procedure's module.

### Duplicate flow names

One flow name declared by several documents collapses to its first declaration: the graph keeps the first document as the flow node, merges the other declarations' facts under it, and warns. The same collision is also exposed structurally as `LineageDuplicateFlowName` (`Name` in the casing of the first declaring document, `Files` as repository-relative paths, ordinal-sorted). Strict consumers use the structured form instead of parsing warning text: the batch orchestrator (src/SqlFlow.Orchestration/BatchOrchestrator.cs) hard-fails a batch whose member name appears in `DuplicateFlowNames`, with the colliding files named, because silently running one declaration and skipping the rest is dangerous in an orchestrated run.

### Warnings

Warnings are never dropped: multiple creators of one object, ambiguous identity resolutions, synonym cycles, unreachable servers, parse warnings, and unhandled statement kinds all surface in `Warnings` (graph-scoped) or on the node (`LineageObjectNode.Warnings`). The list is deduplicated and ordinal-sorted.

## Node and server identity

### The node key

`NodeKey.For` in src/SqlFlow.Lineage/Collection/LineageFacts.cs is THE node-identity rule, shared by collectors, builder, catalog sync, and queries:

```text
serverRef|database|schema|name
```

Each part lowercased, joined with `|`, absent parts empty. Example: `${env:sqlflow_conn_dwh}|dwh|dbo|orders`.

### Server identity

The server part is the connection REFERENCE, not the resolved connection string (`ServerIdentity.From`):

- A value that is one whole `${...}` token or one `@alias` token passes through verbatim. Two documents naming the same reference name the same server by construction.
- Anything else is an inline literal that may carry credentials. It is identified as `inline:` plus the first 4 bytes (8 hex characters) of the SHA-256 of its lowercased, trimmed text, and its content is never echoed. A hybrid such as `${env:HOST};Password=...` counts as a literal and hashes.
- File endpoints (a file flow's source, an export destination) use the constant identity `file` (`ServerIdentity.FileSystem`). Local paths normalize against the estate root, so `./data/x.csv` and `data/x.csv` are one node; locations containing `://` stay verbatim (src/SqlFlow.Lineage/Collection/FlowSetCollector.cs).

### Server aliases

Connected computation proves identities equal: two references resolving to the same canonical connection string are the same server. The ordinal-smallest identity represents the group, the rest alias to it, and the builder rewrites every collected element (flows, facts, catalog objects, synonyms, servers, default databases) onto the canonical identities before anything else (src/SqlFlow.Lineage/Collection/CatalogCollector.cs, `ApplyServerAliases` in the builder). Offline, distinct references stay distinct servers by construction.

### Synonyms

Every fact lands on the base object: synonym chains resolve hop by hop with a 32-hop ceiling (`LineageGraphBuilder.MaxModuleDepth`). A chain exceeding the ceiling is a cycle and warns:

```text
synonym chain at '<serverRef>:<db>.<schema>.<name>' exceeds 32 hops (a synonym cycle); left unresolved.
```

### Default databases and identity unification

A fact without a database is a two-part reference, and the engine resolves those against the connection's default catalog at execution time; the graph applies the exact same resolution, in this order:

1. **Connected ground truth wins.** The derived tier records `DB_NAME()` per reached server (src/SqlFlow.Lineage/Collection/CatalogCollector.cs).
2. **Offline fallback.** `DefaultDatabaseResolver` (src/SqlFlow.Lineage/Collection/DefaultDatabaseResolver.cs) resolves the reference (secret expansion plus canonicalization) and reads `Initial Catalog` off the canonical string, without opening a connection. Only servers that actually carry a database-less fact are resolved; only the database NAME leaves the resolver. A reference that cannot be resolved, or that declares no catalog, degrades to a warning.
3. **Single-candidate unification.** When no default is known, a partial key maps onto the one full identity matching its remaining parts, when exactly one exists. Several candidates stay split and warn, because a wrong merge would silently corrupt the execution order:

```text
'<schema>.<name>' on '<serverRef>' matches N databases (db1, db2); left unresolved.
```

A SQL Server identity (MSSQL or AZDB) still without a database after all of that is a split risk and warns once per identity:

```text
object '<label>' on server '<ref>' has no database identity; it can split from the same object's database-qualified identity elsewhere in the graph.
```

Engines without a database concept are exempt and keep the empty segment by design. Unification runs BEFORE synonym resolution, so a two-part reference to a synonym still matches the synonym map.

## The execution plan

The plan computes the schedule run order deterministically (`ComputeRunOrder` in src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs). It works over each flow's effective relations: the flow's own facts plus module inheritance (a flow reading a view or executing a procedure inherits the module's derived relations transitively, depth-guarded at 32 levels).

### Producer maps and dependency rules

Per object, three producer maps are built in flow order: creators, writers, readers. Multiple creators of one object warn:

```text
object '<key>' is created by multiple flows: a, b.
```

Dependencies per relation:

| A flow that... | Waits for |
| --- | --- |
| `Reads` an object | The object's writers, SKIPPING any writer that reads something this flow writes (the mutual pair would deadlock the plan); falls back to the creators when no safe writer exists. |
| `Requires` an object | The object's creators. |
| `Destroys` an object | The object's creators, writers, AND readers: destruction goes last. |
| `Creates` or `Writes` an object | Nothing; these imply no dependency. |

### Kahn waves

Waves come from a modified Kahn's algorithm with max-level assignment: each flow's level is one more than the maximum level of its dependencies, and the levels are the concurrency waves. Flows in one wave have no dependency between them and can run concurrently; a later wave runs only after every earlier one finished. Waves are 1-based (`LineageWave.Wave`), and each wave's flows are name-sorted.

### Cycle fallback

When Kahn's leaves flows unprocessed, a cycle exists. The plan degrades loudly instead of refusing:

- A path is traced through the unprocessed set (deterministic: smallest index first) and warned: `dependency cycle detected: a -> b -> a.` followed by `affected flows placed in the fallback wave: ...`.
- The cycle is reported as `LineageCycle`: `Flows` in path order (the last depends on the first) plus `ViaObjects`, the object keys mediating the path's edges.
- The members are listed in `ExecutionPlan.Unordered` and placed together in a final fallback wave (max processed level plus one).

`sqlflow lineage --strict` turns cycles into exit code 2; without `--strict` the exit code stays 0.

### Flow dependencies

Every dependency edge is emitted as `LineageFlowDependency`: `FromFlow`, `ToFlow` (ToFlow waits for FromFlow), and `ViaObjects`, the mediating object keys. This is the edge set behind the waves, and the data `sqlflow lineage --explain <flow>` renders (wave placement, upstream dependencies with their waves and mediating objects, downstream dependents, reads and writes with tier provenance).

### Who consumes the waves

The wave is lineage's primary output:

- `sqlflow db sync` stamps each pipeline's wave onto the catalog (`pipeline.Wave` in src/SqlFlow.Catalog/CatalogSync.cs), per repository.
- Batch flows (`flowType: batch`) order their members from the same dependency data: the orchestrator computes the lineage report over the folder, then recomputes concurrency waves over just the included member subset from the report's `FlowDependencies` (the same modified-Kahn, max-level algorithm, scoped to the batch), and runs members wave by wave (src/SqlFlow.Orchestration/BatchOrchestrator.cs). See [batch flows](../flow/batch.md).

### Determinism

Producer maps fill in flow order; dependency edges iterate relations ordinal-sorted by key then relation; `TraceCycle` starts from the smallest index; waves and dependency lists are name-sorted. Same input, same plan, byte for byte.

## facts.json: the pre-merge facts dump

`LineageFactsDump` (src/SqlFlow.Core/Lineage/LineageFactsDump.cs, `CurrentSchemaVersion = 1`) is the debugging companion to the report: the raw collected facts BEFORE the builder's identity unification and synonym resolution, so an operator can see exactly which tier contributed which fact, diff what `--connect` adds over the offline tiers, and trace a surprising dependency to its source.

It is written to `<folder>/.sqlflow/lineage/facts.json` only when `--dump-facts` is passed to `sqlflow lineage` (src/SqlFlow.Cli/Program.cs). Contents:

| Field | Contents |
| --- | --- |
| `Facts` | Raw `LineageRawFact` rows: `Flow`, `ViaModule`, `Relation`, `ServerRef`/`Database`/`Schema`/`Name`, the precomputed `NodeKey`, `Tier`, `Kind`, `ObservedRunId`, `ObservedAtUtc`, `Step`. |
| `CatalogObjects` | The derived tier's inventory (server, database, schema, name, kind, warning). |
| `Synonyms` | Each synonym and its target. |
| `Servers` | Each referenced server: identity, redacted reference, provider kind. |
| `ServerAliases` | Identities proven equal at connect time: alias identity to canonical identity. |
| `Warnings` | Collection-phase warnings. |

The dump and the report come from ONE collection pass: `LineageService.ComputeDetailedAsync` returns both with the same `GeneratedAtUtc` and tier set, so requesting both never connects twice.

Server references in the dump are secret-safe: a whole `${...}` or `@alias` reference echoes verbatim; an inline literal is replaced by `(inline literal redacted)`. Facts are deterministically ordered (tier, then flow, module, node key, relation); `CatalogObjects` and `Synonyms` sort by server, database, schema, name; `Servers` sorts by identity; so dumps diff cleanly across runs.

## Configuration touchpoints

Lineage has no YAML keys of its own; it reads the flow documents as they are. The controls are CLI flags on `sqlflow lineage <folder>`:

| Flag | Effect |
| --- | --- |
| `--connect` | Adds the derived tier: connects to the referenced SQL Servers, inventories the catalog, and expands module definitions. Off by default; the offline tiers stand on their own. |
| `--no-observed` | Drops the observed tier (run artifacts); declared only. |
| `--dump-facts` | Also writes `facts.json` beside `lineage.json`. |
| `--out <path>` / `-o <path>` | Writes the report JSON to an additional path. |
| `--json` | Machine-readable output on stdout. |
| `--explain <flow>` | Explains one flow's wave placement, dependencies, and edges. |
| `--of <subject>` | Impact walk from an object or flow; `--down` (default) walks downstream impact, `--up` walks upstream dependencies. |
| `--strict` | Exit code 2 when the plan contains a cycle. |

`--of` subjects resolve as a flow name, a full node key, or a case-insensitive suffix (`name`, `schema.name`, or `database.schema.name`); an ambiguous subject lists all matches and exits 1. Connection references resolve through the secret resolver; `${env:...}` references read environment variables. `sqlflow db sync ... --connect` runs the same computation and persists objects, edges, dependencies, and waves into the control database.

## Example

The seven-flow estate in samples/lineage-demo lands a five-wave plan: two file landings (wave 1), two ingestions reading the typed views (wave 2), then a three-procedure chain (waves 3 to 5) whose `ing -> sp` and `sp -> sp` dependencies come from the derived tier parsing `sys.sql_modules`.

```bash
sqlflow lineage samples/lineage-demo --connect --dump-facts
```

```text
Lineage over 7 flow(s), ... object(s), ... edge(s) (declared+observed+derived)
  wave 1: demo-land-customers, demo-land-orders
  wave 2: demo-ing-customers, demo-ing-orders
  wave 3: demo-build-order-fact
  wave 4: demo-build-country-rollup
  wave 5: demo-build-exec-kpi
  lineage: .../samples/lineage-demo/.sqlflow/lineage/lineage.json
  facts: .../samples/lineage-demo/.sqlflow/lineage/facts.json
```

Explaining one flow shows the traced rationale:

```bash
sqlflow lineage samples/lineage-demo --connect --explain demo-build-order-fact
```

An edge from that estate, as it appears in `lineage.json`:

```json
{
  "flow": "demo-ing-orders",
  "viaModule": null,
  "relation": "Reads",
  "objectKey": "${env:sqlflow_demo_db}|sqlflowcatalogtests|demo|vorders_pre",
  "tier": "Declared"
}
```

## See also

- [sqlflow lineage (CLI)](../cli/lineage.md)
- [Batch flows](../flow/batch.md)
- [Lineage tiers](lineage-tiers.md)
- [The shadow catalog](shadow-catalog.md)
- [Connections and secrets](connections-and-secrets.md)
