---
id: wiki-api-fanout
title: "Pattern: fetching one endpoint many times (date windows, id lists, discovered ids)"
type: pattern
summary: The three iterate kinds, how they compose as a cartesian product, batching many ids into one request, and why a live id-discovery call hides retired entities.
keywords:
  - iterate
  - date window
  - backfill
  - ids_from
  - idBindings
  - batchSize
  - fan-out
  - granularity
  - overlapMinutes
sourceRefs:
  - src/SqlFlow.Acquire/Engine/AcquireEngine.cs
  - src/SqlFlow.Acquire/Runtime/RelativeTime.cs
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
related:
  - wiki-api-pagination
  - wiki-api-resume-and-watermarks
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: fetching one endpoint many times

**The problem.** One request rarely covers a source. History needs a day-by-day walk; a per-entity
endpoint needs the entity list; a rate-limited vendor needs the ids batched. All three are the same
mechanism: `source.iterate` turns one declared request into many.

Iterations compose **outer to inner as a cartesian product**. Two iterations, thirty days and five
operators, produce 150 request pipelines, each of which may itself paginate.

## date_window: walking history

```yaml
iterate:
  - kind: date_window
    granularity: daily          # hourly | daily | monthly
    from: now-30d               # now, today, yesterday, startOfMonth, now-3d, or an ISO date
    to: now
    fromVariable: window.from
    toVariable: window.to
```

The bound variables are then formatted in templates: `{window.from:yyyy-MM-dd}`. The lower bound also
becomes the reference date for bare tokens like `{yyyy}` inside that step, which is what makes a
landing path partition correctly per step rather than per run.

Granularity is a real decision, not a formality. `hourly` exists because some feeds cap a response by
row count, so a day-sized window silently truncates; `voi/` and `ryde/` walk hourly for that reason.
`monthly` exists because some sources correct history in place, so `svv/` re-reads whole months to
pick up corrections rather than trusting that yesterday is final.

A per-run backfill window (`--from` / `--to`) replaces the declared `from`/`to` for that run, and
also causes the stored watermark to be ignored so the window can actually reach back. Without that,
a watermarked flow would stay pinned at its last position and a backfill could never move.

`overlapMinutes` re-reads a small tail of the previous window, for feeds that commit records slightly
late. Exemplar: `cyclehire/`.

## list: a known set

```yaml
iterate:
  - kind: list
    variable: operatorId
    values: [voi, ryde, tier]
```

Used when the set is stable and small enough to state. Exemplars: `Norled/`, `easypark/`.

## ids_from: a discovered set

```yaml
iterate:
  - kind: ids_from
    variable: bikeId
    idRequest:
      method: GET
      path: /v1/bikes
    idPath: $[*].BikeId
```

The discovery request runs once per outer-iteration combination, with the same auth, and its result
drives the inner loop. This is how a per-entity endpoint gets covered without hardcoding the roster.

**`idBindings` binds several fields per id, not just one.** When the inner request needs more than an
identifier (a per-entity security token, say), `idBindings` maps template variable names to fields in
the discovery response, so each iteration carries the whole tuple:

```yaml
    idBindings:
      questId: QuestId
      securityLock: SecurityLock
```

Exemplar: `questback/`, where each survey's fetch requires both its id and its own lock value.

**`batchSize` puts many ids in one request.** When the endpoint accepts a delimited id list, batching
turns 2,000 requests into 40. `batchSize: 50` with `batchSeparator: ","` binds the variable to a
joined batch rather than a single id. Exemplar: `svv/`.

## The trap: discovery only sees the current fleet

`ids_from` asks the source who exists **now**. Every entity that has been retired, deleted, or
deregistered is absent from that answer, and therefore its entire history is absent from the fetch.
A run that lands more rows than yesterday still looks healthy, which is what makes this hard to spot:
a positive row delta is not evidence of completeness.

When a source has entity churn, the current-fleet list is a live-feed mechanism, not a history
mechanism. History for departed entities has to come from somewhere that still remembers them, which
in practice means an archive or the prior system, backfilled separately.

## Production exemplars

| Flow folder | Fan-out |
| --- | --- |
| `voi/`, `ryde/` | `date_window` hourly, to dodge per-response row caps |
| `svv/` | `date_window` monthly re-read, plus `ids_from` with `batchSize: 50` |
| `cyclehire/` | `ids_from` (bikes then per-bike sessions), `overlapMinutes` |
| `questback/` | `ids_from` with `idBindings` carrying a per-entity lock |
| `Norled/`, `easypark/` | `list` over a fixed operator or route set |
