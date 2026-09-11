---
id: wiki-relational-incremental
title: "Pattern: incrementally ingesting a relational source without missing late rows"
type: pattern
summary: "Date-column windows with overlap, chunked first loads, and pushing a filter into the source without turning an append into a full compare."
keywords:
  - incremental
  - overlapDays
  - dateColumn
  - initLoad
  - source filter
  - filterIsAppend
  - ignoreColumns
  - incrementalClause
  - threads
referenceRefs:
  - flow-incremental
  - concept-ingestion-run-pipeline
  - flow-ing-schema-incremental
  - guide-incremental-and-backfill
  - guide-table-to-table-ingestion
related:
  - wiki-upsert-and-history
  - wiki-api-resume-and-watermarks
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: incrementally ingesting a relational source

**The problem.** A source table has 2 billion rows and gains a few million a day. Reading it whole
every night is not an option, and reading only "since the last run" loses any row that was committed
late or corrected in place.

`flowType: ing` is the dominant flow kind in the estate (512 of 751 production documents), so these
are the highest-leverage patterns here.

## The window, and why it overlaps

```yaml
incremental:
  dateColumn: UpdatedDate
  overlapDays: 3
```

The window runs from the stored watermark back by `overlapDays`, forward to now. The overlap is not
belt-and-braces; it is the mechanism that catches rows committed slightly after the timestamp they
carry. Thirty of thirty-nine source folders declare it, which is a fair signal that late commits are
the norm rather than the exception.

The overlap only helps if the load is a keyed upsert, because it re-presents rows already loaded.
Paired with an append-only load it duplicates them. See [upsert-and-history](upsert-and-history.md).

`incremental.columns` takes several columns when no single one is reliable, for example a source with
both a modified date and a monotonic id where neither alone covers every change.

## Chunking a first load

The first run is a different problem from every run after it. `initLoad` splits it into ranges so one
statement does not have to move the entire table:

```yaml
initLoad:
  enabled: true
  keyColumn: TripId
  batchBy: 500000
  batchSize: 500000
  keyMaxValue: 1800000000
```

Exemplar: `fara/`, across eighteen flows. `load.threads` parallelises the movement where the source
and target can take it. Neither is a substitute for the incremental window; they exist to make the
one-off backfill finish.

## Narrowing at the source

Three keys reduce what crosses the wire, and they are not interchangeable.

**`source.ignoreColumns`** drops columns before they are staged. Use it for columns that are large,
unstable, or simply not wanted; the target never learns about them.

**`source.filter`** pushes a predicate into the source SELECT. This is the right place for a
restriction that is part of the source's meaning ("only settled transactions").

**`source.incrementalClause`** supplies the incremental predicate itself when the natural expression
is not a plain column comparison.

**`filterIsAppend` is the subtle one.** A filter narrows what the source presents. If the load is a
keyed upsert, the engine's INSERT is an anti-join, so rows excluded by the filter are simply absent
rather than deleted, and a row that later stops matching the filter stays behind in the target
forever. `filterIsAppend: true` declares that the filtered set is meant to be appended rather than
reconciled, which is what you want when the filter expresses "the new slice" and not "the valid set".

Getting this backwards is quiet: nothing errors, the counts just stop meaning what you think.

## The reconciliation habit

When a ported table does not match the system it replaced, resist the urge to repoint anything. The
useful sequence is: establish what the flow declares, what actually exists at that endpoint, and
where the data really is; then diff by key, not by count.

Two specific cautions from the estate:

- **Compare byte-sensitively.** A length-based checksum passes on a codepage-mangled Norwegian table.
  Use a real checksum over the values.
- **A count gap is not proof of missing rows.** Dry-run the key anti-join first. Provenance columns
  inside the key make identical rows look absent, and the prior system may itself hold duplicates.

## Production exemplars

| Flow folder | Pattern |
| --- | --- |
| `fara/` | `initLoad` chunked first load plus `load.threads` |
| `apc/`, `fara/` | `source.incrementalClause` for a non-trivial incremental predicate |
| `cyclehiresrel/` | `source.filter` with `filterIsAppend` |
| `apc/`, `kommunedata/`, `mpc/` | `source.ignoreColumns` |
| 30 folders | `incremental.overlapDays` as the default posture |
