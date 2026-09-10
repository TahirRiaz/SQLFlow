---
id: wiki-upsert-and-history
title: "Pattern: upserting, keeping history, and not breaking a downstream consumer"
type: pattern
summary: Choosing a merge key, when append beats upsert, temporal versioning and surrogate keys, and the compatibility view that lets a rebuilt table keep its old shape.
keywords:
  - upsert
  - keyColumns
  - batchUpsert
  - truncateBeforeLoad
  - surrogateKeys
  - temporal
  - scd2
  - system columns
  - compatibility view
referenceRefs:
  - concept-upsert-and-change-detection
  - flow-ing-load
  - flow-ing-versioning
  - concept-provenance-and-row-keys
  - concept-schema-evolution
related:
  - wiki-relational-incremental
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: upserting, keeping history, and not breaking a downstream consumer

**The problem.** Getting rows into a target is the easy half. The hard half is deciding what counts
as the same row, what to do when it changes, and how to do all that without altering the shape some
report has been selecting from for eight years.

## The merge key is the design

`load.keyColumns` decides identity. Everything else follows from it: the upsert is a keyed two-step,
and its INSERT is an **anti-join**, so a row whose key is not already present is inserted.

Two consequences worth internalising:

**NULL-key rows are inserted, not dropped.** An anti-join does not match a NULL key against anything,
so rows with a NULL in a key column insert every single run and accumulate. If the legacy system had a
`WHERE key IS NOT NULL`, that predicate has to be restated as `source.filter` (with `filterIsAppend`
where appropriate), not assumed.

**Provenance columns in the key change what the key means.** Adding the source file to the key makes
each re-export of the same fact a distinct row. Combined with a rolling-window source, the row count
tracks files loaded rather than facts.

`load.batchUpsert` with `batchUpsertRowCount` chunks the merge so one statement does not have to
hold a lock over the whole set. Thirteen folders use it.

## When there is no key

`load.mode: append` skips reconciliation entirely, which is right for immutable event streams (all
138 file-flow documents in the estate use it). `target.truncateBeforeLoad` replaces the table each
run, which is right for a small current-state dimension where the source is authoritative and
history is not wanted.

`truncateBeforeLoad` goes under `target:`. Put it under `load:` and it is silently ignored, because
the loader ignores unmatched properties; see [ignored-yaml-keys](../incidents/ignored-yaml-keys.md).

**Truncate-reload plus a paused schedule loses data permanently** when the source only exposes
current state. There is no window to re-read: whatever was current while the schedule was off is
simply gone. Treat a truncate-reload flow's schedule as load-bearing.

## History

`systemColumns.insertedDate` and `updatedDate` stamp every row, and are the usual basis for a
downstream watermark. Note that a column only stamped on UPDATE freezes a watermark that reads it,
so confirm which operations set the column you are keying on.

`versioning.temporal` puts SQL Server system-versioning on the target, with the history table,
period columns, and precision declared:

```yaml
versioning:
  temporal:
    enabled: true
    historySchema: arc
    historyTable: MyTable_History
    validFromColumn: ValidFrom
    validToColumn: ValidTo
    hiddenPeriodColumns: true
```

It is mutually exclusive with `truncateBeforeLoad` (SQL Server forbids TRUNCATE on a versioned
table) and with `scd2` (two history mechanisms would record every change twice). Both combinations
are rejected up front rather than half-applied. Exemplar: `svv/`.

`surrogateKeys` maintains a key table mapping business keys to a surrogate, which is what a
dimensional consumer joins on. Five folders use it.

## Not breaking the consumer

A rebuilt table almost never matches the hand-built one it replaces column-for-column: the framework
appends its surrogate key and audit columns, so positions shift even when the column set is
identical. A consumer doing `SELECT *` or positional access breaks.

The fix is **not** to reshape the new table. Point the flow at a physical table named for the new
source, then create a view under the **old** name that projects the exact legacy shape: same columns,
same order, same names, same types, each explicitly cast. Downstream keeps querying the old name and
sees no change.

The same technique resolves a name collision between a frozen historical era and a live feed: keep
`_hist` and `_live` tables and union them behind a view under the original name. Split the eras by an
id anti-join rather than a cutover date, because a date boundary and the actual data rarely agree.

## Schema drift

`schema.evolve` lets the target grow to match the source; `schema.sync` keeps them aligned. Both are
preferable to a hand-written ALTER, but neither is a licence to let a source silently change
meaning: a new column is cheap, a retyped column is not.

## Production exemplars

| Flow folder | Pattern |
| --- | --- |
| `svv/` | `versioning.temporal` with hidden period columns |
| `citybike/`, `fara/`, `kommunedata/`, `questback/` | `surrogateKeys` (+ `postProcess` in `svv/`) |
| 13 folders | `load.batchUpsert` with an explicit row count |
| 10 folders | `target.truncateBeforeLoad` for current-state dimensions |
