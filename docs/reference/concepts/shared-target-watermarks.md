---
id: concept-shared-target-watermarks
title: "Incremental watermarks when several flows share one target table"
type: concept
summary: "Why a fan-in of flows into one target starves all but one writer, how to scope the probe with source.incrementalClause, and why overlapDays is inert on a numeric watermark."
keywords:
  - watermark
  - incremental
  - shared target
  - fan-in
  - incrementalclause
  - overlapdays
  - lookback
  - datecolumn
  - discriminator
  - silent data loss
related:
  - guide-incremental-and-backfill
  - flow-incremental
  - concept-ingestion-run-pipeline
  - concept-upsert-and-change-detection
sourceRefs:
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
---

# Incremental watermarks when several flows share one target table

SQLFlow keeps no control database for incremental state: **the target table is the state**. An `ing` flow
decides what to read by probing its target for a watermark and selecting source rows beyond it. That design
is what makes a flow restartable and free of drift, and it holds perfectly as long as one flow owns one
target.

It stops holding the moment **several flows write the same target**. Then the watermark is no longer "how
far this flow has got"; it is "how far *any* writer has got". Every flow but the furthest-ahead one reads
nothing.

## The failure

A fan-in looks like this: N flows, one per partition of the data (per source system, per region, per
tenant), all merging into one table that separates them with a discriminator column.

```
flow A  ─┐
flow B  ─┼──►  arc.Shared_Table   (discriminated by SourceId)
flow C  ─┘
```

Each flow probes `MAX(watermark)` against that shared table. Suppose the flows run in sequence:

1. Flow A probes the shared maximum, reads its own new rows, merges them. The shared maximum is now A's
   latest value.
2. Flow B probes the **same table**, gets A's value, and finds nothing in its own source beyond it.
3. Flow B loads zero rows. So does C.

Three properties make this hard to notice:

- **Every run succeeds.** Reading zero rows is a legitimate outcome, not an error.
- **Upstream stages look healthy.** The stage-1 flows keep landing rows normally; the data simply stops one
  stage short of the target.
- **It never self-corrects.** The leading flow re-advances the shared watermark on every run, so the others
  stay starved indefinitely. Waiting does not help.
- **Row-count reconciliation does not catch it.** The table keeps growing, because the leading writer is
  still working. Only counts *per discriminator value* reveal the gap.

The data is not lost. It accumulates in the upstream staging or landing tables, above each starved flow's
own frontier, waiting for a predicate that will select it.

## The fix: scope the probe

`source.incrementalClause` is appended to the **watermark probe on the target**, not to the source read. Give
each flow the predicate that isolates its own slice:

```yaml
source:
  server: pre
  object: "[db].[pre].[v_Partition_A]"
  # Five flows share this target; an unscoped MAX() returns whatever partition loaded last.
  incrementalClause: "AND [SourceId] = 31"
```

The probe becomes `SELECT MAX(watermark) FROM target WHERE 1=1 AND [SourceId] = 31`, so each flow tracks its
own frontier and the flows stop interfering. The discriminator column must exist on the target; verify that
before rolling the change out, not after.

Recovery usually needs **no backfill**. Once the predicate is right, the first ordinary run selects
everything that was stranded above that flow's own watermark. Reach for `--full` only if the upstream data
has actually been discarded; replaying full history to fix a predicate bug costs orders of magnitude more
work for the same result.

## The related trap: `columns` is not `dateColumn`

Both kinds of watermark can re-read a window behind the mark so late-arriving rows are not missed, but each
kind has its OWN key for it, and using the wrong one is silent:

| Declaration | `IsDate` | Probe expression | Its rewind key |
|---|---|---|---|
| `incremental.columns: [X]` | `false` | `MAX(X) - lookback` (bare `MAX(X)` unless a lookback is set on an arithmetic type) | `incremental.lookback` (key units, default 0) |
| `incremental.dateColumn: X` | `true` | `DATEADD(day, -overlapDays, MAX(X))` | `incremental.overlapDays` (days, default 7) |

`BuildMarkList` stamps every `incremental.columns` entry `IsDate = false`, and only an `IsDate` mark receives
the `DATEADD`. So `columns: [X]` together with `overlapDays: 7` parses, validates and runs while applying no
overlap whatsoever. Nothing warns you.

Choose deliberately:

- A genuine date or datetime column that should tolerate late arrivals belongs under `dateColumn`, with
  `overlapDays`.
- A monotonic surrogate key, or a numeric stamp such as a `decimal` in `yyyyMMddHHmmss` form, belongs under
  `columns`, with `lookback`. Carrying an `overlapDays` alongside it is harmless but misleading; it reads
  like a safety net that is not there, and the safety net that IS there is the one you did not set.

A numeric watermark needs its rewind for the same reason a date one does, not as a nicety: an id is handed
out at `INSERT` and the row becomes readable at `COMMIT`, so a bare `MAX` can advance past an id that is
still in flight and the next run's strict `>` never reaches back down to it. See
[incremental.lookback](../flow/ing-schema-incremental.md#incrementallookback).

The two kinds compose: both sets of predicates are built from the same mark list and ANDed onto the source
read (`BuildPredicates`).

## Diagnosing a suspected case

1. Group the target by its discriminator and compare `MAX(watermark)` and `MAX(business_date)` across the
   values. One value far ahead of the rest is the signature.
2. Check the upstream landing table for rows above each starved value's own watermark. If they are sitting
   there, the diagnosis is confirmed and no backfill is needed.
3. Read the run history for the starved flows: repeated `succeeded` with zero inserted and zero updated,
   while their stage-1 counterparts report healthy row counts.

## When designing a fan-in

Prefer giving each writer its own watermark scope from the start. If you introduce a shared target, add the
scoping predicate in the same change. It is not a tuning knob, it is what makes the incremental contract
correct when the target has more than one owner.
