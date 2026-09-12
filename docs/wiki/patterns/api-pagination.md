---
id: wiki-api-pagination
title: "Pattern: walking a paged endpoint without missing or duplicating records"
type: pattern
summary: The five pagination strategies, how each decides it has reached the end, and why a cursor feed that repeats its cursor needs keyset instead.
keywords:
  - pagination
  - keyset
  - cursor
  - offset
  - link header
  - recordsPath
  - stopOnStatus
  - maxPages
sourceRefs:
  - src/SqlFlow.Acquire/Engine/HttpTransport.cs
  - src/SqlFlow.Acquire/Runtime/JsonPathReader.cs
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
related:
  - wiki-api-resume-and-watermarks
  - wiki-api-fanout
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: walking a paged endpoint without missing or duplicating records

**The problem.** An endpoint returns a slice at a time. Getting every record means knowing how to
ask for the next slice and, harder, how to know there is no next slice. Getting that wrong either
truncates history silently or loops until something breaks.

Each fetched page lands as its own raw file. Pagination is therefore also the unit of landing, not
just of fetching.

## The five strategies

`source.pagination.strategy` selects the state machine.

| Strategy | Advances by | Stops when |
| --- | --- | --- |
| `none` | nothing, one request | always |
| `page` | incrementing `pageParam` from `startPage` | an empty page |
| `offset` | adding `limit` to `offsetParam` | an empty page |
| `cursor_body` | sending the value at `cursorPath` as `cursorParam` | the cursor path yields nothing |
| `link_header` | following RFC 5988 `Link rel="next"` | the header is absent |
| `keyset` | sending the highest id seen as `keysetParam` | empty page, unchanged id, or `stopOnStatus` |

Every strategy is additionally capped by `maxPages`, which is a runaway guard rather than a tuning
knob. Set it comfortably above the real page count.

## recordsPath is what makes "empty" meaningful

`source.pagination.recordsPath` is a JSON path to the record array. It is doing more work than it
looks: it defines empty-page detection, the `landing.skipEmpty` policy, keyset max-id scanning, and
the response watermark. A `page` or `offset` flow without it cannot reliably tell a final empty page
from a page it failed to understand.

```yaml
pagination:
  strategy: offset
  offsetParam: offset
  limitParam: limit
  limit: 500
  recordsPath: $.data
  maxPages: 2000
```

## Why keyset beats cursor on a repeating feed

A `cursor_body` loop trusts the vendor to stop emitting a cursor. Some do not: on the empty final
page they echo the same cursor back, and the flow loops to `maxPages` re-fetching nothing. Keyset
does not depend on vendor discipline. It derives the next position from the data itself, the maximum
id observed across the page, so an empty page has no new maximum and the walk terminates on its own.

Prefer `keyset` whenever the records carry a monotonic id. Reach for `cursor_body` only when they do
not.

## The two keyset id sources

`keysetIdPath` reads the next id from each record in the body. That is the usual case.

`keysetIdHeader` reads it from a response header instead, which is the only option when the body is
not JSON at all. `entur/` fetches binary XLSX reports and takes the next report id from
`X-Entur-Report-Id`, with `stopOnStatus: 202` as the vendor's "no more reports" signal. The sentinel
page is not landed.

```yaml
pagination:
  strategy: keyset
  keysetParam: idAfter
  keysetIdHeader: X-Entur-Report-Id
  stopOnStatus: 202
  maxPages: 20000
```

`stopOnStatus` exists because a clean end-of-sequence signalled by a non-2xx status would otherwise
be an error. It terminates pagination without failing the run. It is not a general error tolerance;
for that see `skipStatusCodes` in [api-resilience](api-resilience.md).

## Pagination inside a fan-out

In the multi-item form each item carries its own `pagination`, so one flow can walk a `page` feed
and an `offset` feed in the same run. When pagination sits under an `iterate`, the page walk restarts
per iteration and `maxPages` applies per iteration, not per run.

`pageVariable` binds the current page number as a template variable, so the landing path can name it
when the default `{page}` discriminator is not the shape you want.

## Production exemplars

| Flow folder | Strategy | Why |
| --- | --- | --- |
| `entur/` | `keyset` + `keysetIdHeader` + `stopOnStatus` | binary XLSX bodies, id only in a header |
| `svv/` | `keyset` with `cursorPath` | monotonic ids in the body |
| `hentmeg/` | `offset` | classic offset/limit REST |
| `fleetreg/` | `page` with `startPage` | page-numbered feed |
| `questback/` | `page` with `pageVariable` | page number needed in the landing path |
