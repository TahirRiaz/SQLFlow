---
id: wiki-recipe-backfill-and-replay
title: "Recipe: backfilling history and replaying what you already landed"
type: narrative
summary: "The flags that reach back past a watermark, why a backfilled arc table stays invisible to the flow above it, and how to replay landed files without re-fetching."
keywords:
  - backfill
  - replay
  - full load
  - from to
  - watermark
  - file-pattern
  - reprocess
  - dry-run
sourceRefs:
  - src/SqlFlow.Acquire/Engine/AcquireFlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
referenceRefs:
  - cli-run
  - guide-incremental-and-backfill
  - guide-source-backfill
  - concept-shared-target-watermarks
related:
  - wiki-api-resume-and-watermarks
  - wiki-relational-incremental
  - wiki-recipe-inspect-and-debug
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: backfilling history and replaying what you already landed

Every incremental flow is pinned by a watermark. These are the three ways past it, and the one trap
that makes a successful backfill look like it worked when it did not.

## The flags

```bash
# ignore the stored watermark entirely, re-read from the declared bounds
sqlflow run vendor/vendor_00_api.yaml --full

# reach back to a specific window (also ignores the watermark, so the window can get there)
sqlflow run vendor/vendor_00_api.yaml --from 2024-01-01 --to 2024-04-01

# re-load only the landed files matching a glob, without re-fetching anything
sqlflow run vendor/vendor_trips_01_jsn.yaml --file-pattern "vendor_trips_2024*.json"

# see what it would do first
sqlflow run vendor/vendor_trips_01_ing.yaml --dry-run --show-sql
```

On an `api` flow, `--from`/`--to` re-window every `date_window` iteration and suppress
`skipUnchanged`, so re-fetched files re-land with fresh timestamps and the flows downstream pick
them up again. Without that, a backfill would land byte-identical files, change no modified times,
and nothing downstream would notice.

## Backfilling in slices

A three-year window in one run is one long-running process with no partial credit. Walk it:

```bash
for y in 2022 2023 2024 2025; do
  sqlflow run vendor/vendor_00_api.yaml --from ${y}-01-01 --to $((y+1))-01-01
done
```

Slice by month where the source is dense or the vendor is rate-limited. Each run is independently
resumable, and a failure costs one slice rather than the lot.

## The trap: a backfilled table is invisible to the flow above it

You backfill `arc.Vendor_Trips` directly (a linked-server transfer, a manual load, a `--from` run of
stage 2). Then you run the `ing` flow that reads it. It reports **SUCCESS, 0 rows inserted**, and
nothing moved.

The reason: that flow's watermark is already past the dates you just inserted. Old rows below the
mark are never looked at. The run is not broken and the report is not wrong; the window simply does
not cover them.

```bash
# fix: make the upper flow re-read the range you touched
sqlflow run vendor/vendor_dim_trips_02_ing.yaml --from 2024-01-01 --to 2024-04-01

# or, when the table is small enough, re-read everything
sqlflow run vendor/vendor_dim_trips_02_ing.yaml --full
```

**Always re-run the flow ABOVE the table you backfilled, windowed to the range you touched.** A
backfill that stops at the table it filled leaves every downstream table short, and the shortfall is
silent.

## Replaying landed files

The lake is the record. When the transform was wrong rather than the fetch, replay without touching
the vendor:

```bash
# re-run stage 1 over everything already landed
sqlflow run vendor/vendor_trips_01_jsn.yaml --full

# or just the slice you care about
sqlflow run vendor/vendor_trips_01_jsn.yaml --file-pattern "vendor_trips_2024*.json"
```

This is why acquisition lands verbatim: a wrong type decision costs a view rewrite and a replay, not
a re-download from a vendor who may no longer serve that history. See
[string-first-landing](../decisions/string-first-landing.md).

## Replaying a retired source

A dead source's flows stay in the repository as `mode: disabled`, which keeps their lineage and
history while excluding them from schedule fires. Naming a flow directly still runs it:

```bash
sqlflow run moveabout/moveabout_trips_01_jsn.yaml --full
```

That is the whole point of disabling rather than deleting: the replay path still exists.

## Order of operations for a full source backfill

```bash
# 1. acquisition, sliced
sqlflow run vendor/vendor_00_api.yaml --from 2022-01-01 --to 2023-01-01
sqlflow run vendor/vendor_00_api.yaml --from 2023-01-01 --to 2024-01-01

# 2. pre, over everything now landed
sqlflow run vendor/vendor_trips_01_jsn.yaml --full

# 3. ods, windowed to what stage 2 actually needs to re-read
sqlflow run vendor/vendor_trips_02_ing.yaml --from 2022-01-01 --to 2024-01-01

# 4. anything reading arc, same window
sqlflow run vendor/vendor_dim_trips_03_ing.yaml --from 2022-01-01 --to 2024-01-01

# 5. confirm nothing was skipped
sqlflow lineage vendor/ --strict
```

Skipping step 4 is the single most common way a backfill ends up half-done.

## Before you transfer rows between estates

A count gap is not proof of missing rows. Dry-run the key anti-join first: provenance columns inside
the merge key make identical rows look absent, and the older system may hold duplicates of its own.
Compare with a real checksum over values rather than a length sum, which passes happily on a
codepage-mangled Norwegian table.
