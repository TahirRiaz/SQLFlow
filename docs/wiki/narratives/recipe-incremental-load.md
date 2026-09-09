---
id: wiki-recipe-incremental-load
title: "Recipe: choosing an incremental load, and the two ways it silently does nothing"
type: narrative
summary: "A decision tree for the watermark, the columns-versus-dateColumn trap that disables your overlap without warning, and how each stage of a chain resumes."
keywords:
  - incremental
  - watermark
  - dateColumn
  - columns
  - overlapDays
  - lookback
  - late arriving
  - recipe
sourceRefs:
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
referenceRefs:
  - flow-incremental
  - concept-shared-target-watermarks
  - guide-incremental-and-backfill
  - flow-ing-schema-incremental
related:
  - wiki-recipe-fan-in-shared-target
  - wiki-recipe-backfill-and-replay
  - wiki-relational-incremental
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: choosing an incremental load

**There is no control database. The target table is the state.** A flow decides what to read by
probing its own target for a watermark and selecting source rows beyond it. That is what makes a run
restartable, and it is why every subtlety below is about the probe rather than about bookkeeping.

## Decision tree

```
Is the resume column a real date/datetime that can arrive late?
  YES -> incremental.dateColumn: X    with overlapDays: N
  NO  -> is it monotonic (an id, or a numeric yyyyMMddHHmmss stamp)?
           YES -> incremental.columns: [X]    with lookback: N
           NO  -> is the table small / current-state only?
                    YES -> target.truncateBeforeLoad: true   (no watermark at all)
                    NO  -> load.mode: append                 (immutable event stream)

Do several flows write this same target?
  YES -> add source.incrementalClause scoping the probe to this flow's slice
```

## The two shapes

```yaml
# a real date column that tolerates late arrivals
incremental:
  dateColumn: UpdatedDate
  overlapDays: 3
```
probe becomes `DATEADD(day, -3, MAX(UpdatedDate))`.

```yaml
# a monotonic id or numeric stamp
incremental:
  columns: [FileDate_DW]
  lookback: 0
```
probe becomes `MAX(FileDate_DW) - lookback`.

## Trap 1: `columns` does not accept `overlapDays`

This is the one that costs the most, because it parses, validates, runs, and reports success:

```yaml
incremental:
  columns: [FileDate_DW]
  overlapDays: 7        # PARSES. RUNS. APPLIES NO OVERLAP WHATSOEVER.
```

Every `incremental.columns` entry is stamped `IsDate = false`, and only an `IsDate` mark receives the
`DATEADD`. The overlap you thought you set is not there, and the rewind that IS available for this
shape (`lookback`) is the one you did not set.

Nothing warns you. It reads like a safety net and is decoration.

```yaml
# correct, for a numeric stamp
incremental:
  columns: [FileDate_DW]
  lookback: 1

# correct, for a genuine date
incremental:
  dateColumn: FileDate
  overlapDays: 7
```

The estate carries the misleading `columns` + `overlapDays` combination widely. It is harmless where
the upstream restates a rolling window anyway, and it is a real hole where it does not.

## Trap 2: a numeric watermark skips late commits

An id watermark assumes ids commit in order. When a source assigns the id at creation and commits the
row later, a run that advances past it never looks back, and the gap is permanent.

The signature is a **contiguous block of missing ids starting at watermark+1**, not a scatter.

```yaml
incremental:
  columns: [trip_id]
  lookback: 5000     # trade a little re-reading for not losing rows
```

## Trap 3: several flows, one target

An unscoped `MAX()` against a shared table returns whatever partition loaded last, so every flow but
the furthest-ahead reads nothing, forever, while succeeding. See
[recipe-fan-in-shared-target](recipe-fan-in-shared-target.md), which is the whole story.

## How each stage of a chain resumes

The three stages resume on different things, which is why one stage catching up does not mean the
next one did.

| Stage | Resumes from | Declared as |
| --- | --- | --- |
| `api` acquisition | the run record, the lake, or a query against loaded data | `incremental.source: response \| lake \| sql` |
| `file` (stage 1) | the file-provenance stamp of what it already loaded | `incremental.dateColumn: FileDate_DW` |
| `ing` (stage 2/3) | its own target table | `incremental.columns` / `dateColumn` |

```yaml
# stage 1: files land with FileDate_DW; overlapDays 0 because nothing arrives late into a landed file
incremental:
  dateColumn: FileDate_DW
  overlapDays: 0
```

```yaml
# stage 2: re-read a week of file dates so an amended export is merged again
incremental:
  columns: [FileDate_DW]
  overlapDays: 7          # see Trap 1: this applies NO overlap. Use lookback, or dateColumn.
```

An acquisition watermark declared `response` lives in the node-local run record and does not survive
a redeploy or a rename. Use `sql` where losing it would lose data:

```yaml
incremental:
  source: sql
  connection: ${env:SQLFLOW_CONN_ODS}
  query: "SELECT CONVERT(varchar(10), DATEADD(day,-7,MAX(EventDate)), 23) FROM arc.Vendor_Trips"
  bindVariable: since
  seed: "2016-01-01"
```

## Recovering after you fix a predicate

Usually no backfill is needed. Once the probe is right, the next ordinary run selects everything
stranded above that flow's own frontier, because the data was never lost, only unselected.

```bash
sqlflow run vendor/vendor_trips_02_ing.yaml            # try this first
sqlflow run vendor/vendor_trips_02_ing.yaml --full     # only if upstream data was actually discarded
```

Reach for `--full` last. Replaying full history to fix a predicate bug costs orders of magnitude more
work for the same result.

## Verifying the window is what you think

```bash
sqlflow run vendor/vendor_trips_02_ing.yaml --dry-run --show-sql
```

Read the probe in the printed SQL. It states the watermark expression, the overlap or lookback
actually applied, and any `incrementalClause`. That one command settles every trap on this page.
