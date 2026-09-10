---
id: cli-lineage
title: sqlflow lineage
type: cli-command
summary: "Compute tiered AST lineage over a flow-estate folder: execution waves, cycles, impact walks, per-flow wave rationale, and the lineage.json artifact."
keywords:
  - lineage
  - tiers
  - impact analysis
  - waves
  - explain
  - lineage.json
  - cycles
  - upstream
  - downstream
cliCommand: lineage
related:
  - concept-lineage-tiers
  - concept-lineage-graph-and-plan
  - guide-lineage-demo
  - cli-db
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Lineage/LineageService.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/RunArtifactCollector.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Core/Lineage/FlowExplanation.cs
---

# sqlflow lineage

## Synopsis

```bash
sqlflow lineage <folder> [--connect] [--no-observed]
                [--of <object|flow>] [--up | --down]
                [--explain <flow>]
                [--dump-facts] [--strict] [--json] [--out <file> | -o <file>]
```

## Description

Computes AST-based lineage over a flow estate: the folder tree containing your `*.flow.yaml` documents. Every `*.flow.yaml` under `<folder>` (recursively) joins the graph. The command builds a `LineageReport` (flows, objects, tier-tagged edges, flow dependencies, an execution plan of concurrency waves, and any dependency cycles), always writes the canonical artifact `<folder>/.sqlflow/lineage/lineage.json`, and prints either a human summary or JSON.

Facts come from up to three tiers, and every edge carries the tier that vouches for it:

| Tier | Source | Availability |
| --- | --- | --- |
| declared | The flow documents (YAML): what the author intends | Always on |
| observed | Canonical run artifacts (`run.json` SqlTrace) under the `.sqlflow/runs` directories next to each flow document and under the estate root, each flow's latest run: what actually executed, stamped with the run | On by default; `--no-observed` drops it |
| derived | Live catalog metadata plus `sys.sql_modules` definitions parsed as T-SQL (ScriptDom): views and procedure bodies expand into module edges, synonyms resolve to their bases, columns and object kinds are captured, encrypted modules surface as node warnings | Only with `--connect` |

The offline tiers never require connectivity; `--connect` is the only switch that touches a database. After collection, two-part object names resolve against each server's default database: recorded from `DB_NAME()` for servers the connected pass reached, otherwise taken offline from the connection reference's `Initial Catalog`.

The CLI is a thin shell over `LineageService.ComputeDetailedAsync` in src/SqlFlow.Lineage/LineageService.cs, the same front door the tests and the catalog sync use, so lineage has exactly one code path. Connection references (`${env:...}`, `@alias`, or inline strings) resolve through the standard secret resolver; the git-ignored `.sqlflow/env` file nearest to `<folder>` (searched upward) is applied first, and the process environment always wins.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<folder>` | yes | The flow-estate root. Every `*.flow.yaml` under it, recursively, is collected. A missing directory fails with `ERROR  Flow directory not found: '<full path>'.` (the argument resolved to a full path) and exit code 1; omitting the argument prints usage and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--connect` | flag | off | Adds the derived tier: connects to every referenced SQL Server, reads catalog metadata and `sys.sql_modules`, and expands module bodies into the graph. |
| `--no-observed` | flag | off | Drops the observed tier; the graph is built from the declared tier only (plus derived, if `--connect`). |
| `--of <subject>` | string | none | Impact walk from an object or flow. The subject is a flow name, a full node key, or a case-insensitive suffix (`name`, `schema.name`, or `database.schema.name`). |
| `--up` | flag | off | With `--of`: walk upstream (what the subject depends on). |
| `--down` | flag | off | With `--of`: walk downstream (what the subject impacts). This is the default direction; the flag states it explicitly. Combining `--up` and `--down` is rejected. |
| `--explain <flow>` | string | none | Print the wave rationale for one flow: its wave, cycle membership, dependencies with mediating objects, tier-tagged reads and writes, and dependents. Takes precedence over `--of` when both are given. |
| `--dump-facts` | flag | off | Also write `<folder>/.sqlflow/lineage/facts.json`: the raw pre-merge facts per tier, before identity unification and synonym resolution. |
| `--strict` | flag | off | Exit 2 when the report contains dependency cycles. |
| `--json` | flag | off | Print JSON instead of the human summary (see Output below). |
| `--out <file>`, `-o <file>` | string | none | Additionally write the full report JSON to an explicit path, regardless of `--of` or `--explain`. |

## Behavior and output

### Artifacts

- `<folder>/.sqlflow/lineage/lineage.json` is always written (the directory is created if needed). It is the full `LineageReport`: `schemaVersion` (currently 1), `generatedAtUtc`, `flowDirectory`, `tiersUsed`, `flows`, `objects` (with columns, module definitions, and node warnings from the derived tier), `edges`, `flowDependencies`, `executionPlan` (`waves` plus `unordered`), `cycles`, `duplicateFlowNames`, and `warnings`.
- With `--dump-facts`, `facts.json` lands beside it: per-tier raw facts with node keys and observed-run provenance, catalog objects, synonyms, servers, server aliases, and warnings, all deterministically ordered. Whole references (`${...}` or `@alias`) are echoed; an inline connection literal is never written and appears as `(inline literal redacted)`.
- An artifact write failure (IO or permissions) prints `WARN  could not write lineage artifacts: <message>` to stderr, omits the artifact path lines from the summary, and does not change the exit code.
- All JSON (artifacts, `--out`, and stdout) is indented, camelCase, with enums as strings.

### Node identity

Object node keys are the canonical identity `serverref|database|schema|name`: the connection reference the object was reached through, then database, schema, and name, all lowercased and joined with `|` (absent parts empty). Two documents naming the same reference name the same server; the connected pass additionally merges references proven equal at connect time (they resolve to the same canonical connection string).

### Default text output

Without `--of` or `--explain`:

```text
Lineage over <F> flow(s), <O> object(s), <E> edge(s) (declared+observed)
  wave 1: <flow>, <flow>
  wave 2: <flow>
  CYCLE  <flow-a> -> <flow-b> -> <flow-a> via <object keys>
  WARN  <report warning>
  lineage: <folder>/.sqlflow/lineage/lineage.json
  facts: <folder>/.sqlflow/lineage/facts.json
```

The tier list in the header is exactly the tiers used, lowercased and joined with `+` (`declared`, `declared+observed`, `declared+observed+derived`, or `declared+derived`). One `wave` line prints per execution-plan wave; when cycles exist, the flows whose order is unresolved (the cycle members and every flow downstream of them) are scheduled together in a final fallback wave whose line reads `wave <N> (fallback: unresolved order): <flows>`, and each cycle prints a `CYCLE` line with its path and mediating objects. The artifact paths print as absolute paths (`<folder>` resolved to its full path). The `facts:` line appears only with `--dump-facts`.

With plain `--json` (no `--of`, no `--explain`), the full `LineageReport` prints to stdout instead, and nothing else is printed.

### Impact walks: --of

`--of <subject>` resolves the subject via `LineageService.ResolveSubject`:

1. A case-insensitive flow-name match wins and becomes `flow:<name>`.
2. Otherwise an exact (ordinal) node-key match wins. Keys are lowercase, so pass suffixes rather than hand-built mixed-case keys.
3. Otherwise a case-insensitive suffix match on `name`, `schema.name`, or `database.schema.name` runs across all objects.

Zero matches: `ERROR  '<subject>' matches nothing in the graph.` (exit 1). Multiple matches: `ERROR  '<subject>' is ambiguous: <key>, <key>. Use the full key.` (exit 1). `--up` combined with `--down`: `ERROR  choose either --up (dependencies) or --down (impact), not both.` (exit 1).

The walk is a breadth-first traversal over data-flow adjacency: a writing or creating flow points at its object, an object points at the flows that read or require it, a base object points at the module that reads or requires it, and a writing module points at its object. `--up` traverses the reversed graph. The result is the ordinal-sorted set of reached identities, excluding the subject itself; flows appear as `flow:<name>`.

Text form (after the header line):

```text
  downstream of <key>: <N> node(s)
    <node key or flow:name>
```

JSON form:

```json
{
  "subject": "flow:demo-ing-orders",
  "direction": "downstream",
  "nodes": ["..."]
}
```

### Wave rationale: --explain

`--explain <flow>` looks the flow up case-insensitively and prints, purely from the report (no extra collection pass):

```text
  flow '<name>' (<kind>): wave <N>
    depends on:
      <flow> (wave <N>) via <object keys>
    reads   [<tier>] <object key>  (run <8-hex>, <step>)
    writes  [<tier>] <object key>
    required by: <flow> (wave <N>), <flow> (wave <N>)
```

- The header appends `(in a dependency cycle)`, preceded by a space, when the flow is a cycle member; waves are 1-based.
- A root flow prints `depends on: (nothing in the set; a wave-1 root)`.
- `reads` lines cover `Reads` and `Requires` edges; write lines are labelled with the lowercased relation (`writes`, `creates`, `destroys`). Each line carries the tier in brackets, and observed edges append `(run <first 8 hex of the run id>, <step>)`, with the step omitted when the trace did not record one.
- With `--json`, the `FlowExplanation` record (`flow`, `kind`, `wave`, `inCycle`, `dependsOn`, `requiredBy`, `reads`, `writes`) prints instead.

A subject that is not a flow fails with `ERROR  '<name>' is not a flow in the graph (use --of for an object's impact walk).` (exit 1). Use `--of` for objects.

## Examples

Offline lineage (declared plus observed) over the sample estate in samples/lineage-demo:

```bash
sqlflow lineage samples/lineage-demo
```

Connected lineage: the derived tier reads the procedure bodies from `sys.sql_modules`, so the `ing -> sp` and `sp -> sp` dependencies appear and the seven demo flows land in five waves:

```bash
sqlflow lineage samples/lineage-demo --connect
```

```text
Lineage over 7 flow(s), <O> object(s), <E> edge(s) (declared+observed+derived)
  wave 1: demo-land-customers, demo-land-orders
  wave 2: demo-ing-customers, demo-ing-orders
  wave 3: demo-build-order-fact
  wave 4: demo-build-country-rollup
  wave 5: demo-build-exec-kpi
  lineage: <absolute path to samples/lineage-demo>/.sqlflow/lineage/lineage.json
```

Downstream impact of an object, addressed by `schema.name` suffix:

```bash
sqlflow lineage samples/lineage-demo --connect --of demo.Orders
```

Upstream dependencies of a flow, as JSON:

```bash
sqlflow lineage samples/lineage-demo --connect --of demo-build-exec-kpi --up --json
```

Why is a flow in its wave:

```bash
sqlflow lineage samples/lineage-demo --connect --explain demo-ing-orders
```

This prints the rationale in the format above: `demo-ing-orders` is an `ing` flow in wave 2; it depends on `demo-land-orders` (wave 1); its reads (the typed view `demo.vOrders_Pre`) and writes (`demo.Orders`) print as node keys with their tier labels, the observed ones stamped with the sample's shipped runs; and `demo-build-order-fact` (wave 3) appears under `required by`.

CI gate: fail the build on dependency cycles and keep the machine-readable report:

```bash
sqlflow lineage flows/ --strict --json -o artifacts/lineage.json
```

Debug what each tier contributed, before merging:

```bash
sqlflow lineage samples/lineage-demo --dump-facts
```

## Exit behavior

| Code | When |
| --- | --- |
| 0 | The report was computed (including when it has cycles, unless `--strict`). |
| 1 | Missing `<folder>` argument, folder not found, an unresolvable or ambiguous `--of` subject, `--up` combined with `--down`, or `--explain` naming something that is not a flow. |
| 2 | `--strict` was set and the report contains at least one dependency cycle. |

An artifact write failure alone never changes the exit code; it only prints a `WARN` line to stderr.

## See also

- [Lineage tiers](../concepts/lineage-tiers.md)
- [Lineage graph and execution plan](../concepts/lineage-graph-and-plan.md)
- [Lineage demo walkthrough](../guides/lineage-demo.md)
- [sqlflow db](db.md)
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
