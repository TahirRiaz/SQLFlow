---
id: wiki-api-resume-and-watermarks
title: "Pattern: resuming an acquisition where the last run stopped"
type: pattern
summary: "The three watermark sources, why a node-local run record is the fragile one, and how a per-entity SQL watermark keeps a fan-out feed from resetting."
keywords:
  - watermark
  - incremental
  - resume
  - keyVariable
  - seed
  - lookback
  - run history
  - durability
sourceRefs:
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
  - src/SqlFlow.Acquire/Engine/AcquireEngine.cs
  - src/SqlFlow.Acquire/Engine/AcquireWatermarkHistory.cs
  - src/SqlFlow.Acquire/Engine/AcquireFlowRunner.cs
referenceRefs:
  - concept-shared-target-watermarks
  - guide-incremental-and-backfill
related:
  - wiki-api-pagination
  - wiki-api-fanout
  - wiki-census-drift
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: resuming an acquisition where the last run stopped

**The problem.** A daily flow should fetch what is new, not re-fetch everything. That requires
remembering a position across runs, and the interesting question is not how to advance the mark but
**where to keep it so it survives**.

Only a successful run advances the watermark, so a failed run never skips the data it failed on.
A full-load run (`--full`) or a backfill window ignores the stored mark entirely.

## The three sources

`incremental.source` picks where the resume point comes from.

| Source | Reads from | Survives a redeploy? |
| --- | --- | --- |
| `response` | the max of `incremental.column` across the run, persisted in the run record | **no** |
| `lake` | what is already landed in the lake | yes |
| `sql` | a scalar query the flow supplies against loaded data | yes |

`response` is the simplest and the default, and it is the one to be careful with. The value lives in
the flow's own run history under `.sqlflow/runs`, which is node-local. A redeploy, a new worker node,
or an edit that changes the flow's identity loses it, and the flow silently resumes from its `seed`.
For a feed whose history is still retrievable that is merely wasteful. For a feed that only serves a
recent window, it is data loss.

`sql` was added for exactly that case: the loaded table survives both a redeploy and a flow edit, and
it is the state the pipeline is actually tracking.

## The per-entity watermark

A fan-out feed has a position **per entity**, not one for the whole flow. A single global mark drags
every entity back to the oldest member's position on every run, which is both slow and, on a
windowed source, lossy.

`incremental.keyVariable` names the fan-out variable the watermark is keyed by. The query then
returns two columns: the key, and that key's resume point.

```yaml
incremental:
  source: sql
  connection: ${env:SQLFLOW_CONN_ODS}
  query: >-
    SELECT SurveyId, CONVERT(varchar(10), DATEADD(day, -7, MAX(EventDate)), 23)
      FROM arc.Survey_Responses
     GROUP BY SurveyId
  keyVariable: surveyId
  bindVariable: since
```

For each fan-out combination, the row whose key matches that iteration's `surveyId` supplies `since`;
an entity with no row falls back to `seed`. One column instead of two means one watermark for the
whole flow.

The engine takes the query verbatim. It composes nothing and knows nothing about the table or column
involved, so the shape of the resume point is entirely the flow's decision. That is why the example
above can subtract seven days inside the query: the lookback is expressed where the flow author can
see it, not hidden in engine behaviour.

Returning no row, or NULL, leaves the flow on its `seed`.

## Binding the watermark into the request

`bindVariable` names the template variable the resolved mark is bound to at run start, so the request
can send it:

```yaml
request:
  query:
    since: "{since}"
```

Keyset pagination is the exception: it reads the watermark directly and needs no binding. See
[api-pagination](api-pagination.md).

## Numeric watermarks skip late commits

An id-based watermark assumes ids are committed in order. When a source assigns an id at creation but
commits the row later, a run that advances past that id never sees it, and the gap is permanent. The
gaps are contiguous blocks starting at watermark+1, which is the signature to look for. A deliberate
lookback (subtracting from the mark, as above, or the ingestion-side `incremental.lookback`) trades a
little re-reading for not losing rows.

## Not available with items[]

`incremental` is single-endpoint only. One watermark per run cannot serve several endpoints, so a
multi-item flow expresses incrementality through each item's own date-window `iterate` instead.

## Production exemplars

| Flow folder | Shape |
| --- | --- |
| `questback/` | `sql` source, per-entity via `keyVariable`, seven-day lookback in the query |
| `entur/` | `response` source with `seed`, driving keyset pagination |
| `reisefrihet/` | ingestion-side `incremental.lookback` against late-committing ids |
