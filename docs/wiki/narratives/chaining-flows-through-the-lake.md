---
id: wiki-chaining-flows-through-the-lake
title: "How flows chain: the lake path and the pre view are the only two joints"
type: narrative
summary: "Flows are never wired to each other; they are joined by naming the same artifact. Two joints do all the work, and lineage derives the waves from them."
keywords:
  - chaining
  - lineage
  - waves
  - lake path
  - az normalization
  - pre view
  - composition
  - hand-off
sourceRefs:
  - src/SqlFlow.Lineage/LineageService.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
referenceRefs:
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - cli-lineage
  - flow-overview
related:
  - wiki-recipe-connect-any-source
  - wiki-recipe-api-source
  - wiki-recipe-vendor-file-source
  - wiki-recipe-database-source
  - wiki-pattern-catalog
updated: 2026-09-10
---

# How flows chain: the lake path and the pre view are the only two joints

There is no `dependsOn` key. Flows are joined by **naming the same artifact**, and lineage derives
the execution order from that. Two joints do all the work.

```
 api ─┐                                                       JOINT 1: a lake path
 cpy ─┼──> raw/<source>/...  ──> file flow ──> pre.<Table>     JOINT 2: the pre view
sftp ─┘        (files)         (csv/jsn/xml/xls)  │
                                                  └─> pre.v_<Table> ──> ing flow ──> arc.<Table>
                                                                           │
                                                                           └──> ing ──> edw/dim
```

Measured over the production estate (`sqlflow lineage`, 708 flows, 282 dependencies):

| Hand-off | Joined by | Count |
| --- | --- | --- |
| `cpy` -> `file` | lake path | 96 |
| `api` -> `file` | lake path | 40 |
| `sftp` -> `file` | lake path | 6 |
| `sftp` -> `cpy` | lake path | 1 |
| `cpy` -> `cpy` | lake path | 2 |
| `file` -> `ing` | pre view | 124 |
| `ing` -> `ing` | table | 13 |

## Joint 1: the lake path

**Every file producer writes a path. Every file consumer reads a path. They chain when the paths
match after normalization.**

The URI form does not have to match. SQLFlow normalizes `abfss://`, `https://`, and `az://` to one
canonical key, so this producer and this consumer bind even though they are written differently:

```yaml
# producer: vendor_00_api
landing:
  target: abfss://fs@account.dfs.core.windows.net/raw/vendor/api/alerts
  pathTemplate: "history/{yyyy}/{MM}/vendor_alerts_{yyyyMMdd}"
  format: json
```

```yaml
# consumer: vendor_alerts_01_jsn
source:
  type: json
  location: https://account.dfs.core.windows.net/fs/raw/vendor/api/alerts/history/
  options:
    srcFile: "vendor_alerts*.json"
    searchSubDirectories: "true"
```

```
lineage objectKey: file|||az://account/fs/raw/vendor/api/alerts/history
```

The three rules that make it bind:

1. **`location` = `target` + the STATIC prefix of `pathTemplate`.** Here `history/`. Everything from
   the first `{token}` onward is dynamic and belongs to the glob, not the path.
2. **`srcFile` matches the literal part of the template's file name.** `vendor_alerts_{yyyyMMdd}`
   becomes `vendor_alerts*.json`.
3. **`searchSubDirectories: "true"`** whenever the template partitions (`{yyyy}/{MM}`), or the
   consumer sees only the top folder and loads nothing.

### How each producer declares its path

```yaml
# api: landing.target (+ items[].landing.target in the multi-item form)
landing:
  target: abfss://fs@account.dfs.core.windows.net/raw/vendor/api/trips
  pathTemplate: "history/{yyyy}/{MM}/vendor_trips_{yyyyMMdd}"
```

```yaml
# cpy: one items[] entry per dataset, each with its own source and target location
flowType: cpy
operation: copy
options: { overwrite: true, preserveStructure: true }
items:
  - source: { location: abfss://fs@old.dfs.core.windows.net/raw/region_a/history/Calls,
              pattern: "*.csv", recursive: true, modifiedWithinDays: 7 }
    target: { location: abfss://fs@new.dfs.core.windows.net/raw/region_a/history/Calls }
```

```yaml
# sftp: `local` is where files land, but lineage needs the explicit `output` block
flowType: sftp
direction: download
remotePath: "."
pattern: "partner_export*.xlsx"
modifiedWithinDays: 4
local: https://account.dfs.core.windows.net/fs/raw/partner/history
preserveStructure: false
output:
  location: https://account.dfs.core.windows.net/fs/raw/partner/history/
  srcFile: "partner_export*.xlsx"
```

**`cpy` and `api` bind implicitly from their target/landing. `sftp` does not: without `output`, the
graph has a hole exactly where the data enters.** Declare `output` (or `outputs`) on every `sftp`
flow whose files something else reads.

### Chaining producers to producers

A producer can read another producer's output, which is how a staged migration works:

```
partner_00_sftp  ──> raw/partner/history/ ──> partner_archive_00_cpy   (sftp -> cpy)
staging_origin_00_cpy ──> raw/staging/datamart/origin ──> staging_bridge_00_cpy
```

Same rule: the downstream `items[].source.location` equals the upstream target location.

## Joint 2: the pre view

A file flow lands a physical table and generates a typed view over it. The `ing` flow reads the
**view**, never the table.

```yaml
# vendor_alerts_01_jsn  (file flow)
target:
  connection: ${env:SQLFLOW_CONN_PRE}
  schema: pre
  table: Vendor_Alerts          # lands pre.Vendor_Alerts (all columns string)
transform:
  generateView: true             # builds pre.v_Vendor_Alerts (typed)
  columns:
    - { name: AlertId,    expr: "CAST(@ColName AS DECIMAL(11,0))" }
    - { name: ReceivedOn, expr: "COALESCE(TRY_CONVERT(datetime,@ColName,127),TRY_CONVERT(datetime,@ColName,21))" }
```

```yaml
# vendor_alerts_02_ing  (ing flow)
flowType: ing
source:
  server: pre
  object: "[StagingDb].[pre].[v_Vendor_Alerts]"     # THE VIEW
target:
  server: ods
  object: "[WarehouseDb].[arc].[Vendor_Alerts]"
```

The view name is `v_` + the file flow's `target.table`. Lineage resolves through it: the edge
records the view as `viaModule` and the underlying table as `objectKey`, so renaming the file flow's
`target.table` silently breaks the chain.

`@ColName` in a transform expression is the placeholder for the column named by `name`. That is what
makes the typed view the place where legacy type contracts are reproduced exactly.

## Reading the chain back

```bash
# the whole estate: waves, cycles, warnings
sqlflow lineage flows/

# machine-readable, for diffing or scripting
sqlflow lineage flows/ --json -o artifacts/lineage.json

# why is this flow in the wave it is in?
sqlflow lineage flows/ --explain vendor_alerts_02_ing

# what feeds this object, or what does this flow feed?
sqlflow lineage flows/ --of arc.Vendor_Alerts --up
sqlflow lineage flows/ --of vendor_00_api --down

# fail CI when the graph has a problem
sqlflow lineage flows/ --strict --json -o artifacts/lineage.json
```

`flowDependencies` in the JSON is the joint list: each entry is `fromFlow`, `toFlow`, and the
`viaObjects` that bound them. A chain you expected that is missing from it is a naming mismatch, and
the `viaObjects` key shows which side normalized to what.

```bash
# the fastest way to find a broken chain: does the object appear once, or twice with different keys?
sqlflow lineage flows/ --json -o /tmp/l.json
grep -o 'az://[^"]*vendor[^"]*' /tmp/l.json | sort -u
```

## Waves fall out of the joints

The estate resolves to four waves, with no ordering declared anywhere:

| Wave | Flows | What is in it |
| --- | --- | --- |
| 0 | 440 | acquisition (`api`, `cpy`, `sftp`) plus every `ing` with no upstream in the repo |
| 1 | 142 | `file` flows reading wave-0 lake paths |
| 2 | 124 | `ing` flows reading wave-1 pre views |
| 3 | 2 | `ing` flows reading wave-2 arc tables |

One schedule fires the whole source in this order. See
[orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) for the schedule side.

## What does not chain by itself

`sp` flows do not appear in `flowDependencies` from a plain `sqlflow lineage` run. Their edges come
from the derived tier, which expands the procedure body against the live catalog:

```bash
sqlflow lineage flows/ --connect        # needs a live connection; expands sys.sql_modules
```

Without `--connect` a stored procedure is a node with no edges, so a chain that runs through one
looks broken when it is not.
