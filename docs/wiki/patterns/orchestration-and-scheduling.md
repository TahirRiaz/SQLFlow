---
id: wiki-orchestration-and-scheduling
title: "Pattern: ordering a source's flows, and retiring one without deleting it"
type: pattern
summary: "One schedule per source on its anchor, wave ordering by lineage rather than batch, chaining with after, and retiring a flow without losing it."
keywords:
  - schedule
  - cron
  - catchup
  - after
  - wave
  - batch
  - mode disabled
  - lineage
  - orchestration
referenceRefs:
  - flow-schedule
  - flow-batch
  - concept-lineage-graph-and-plan
  - concept-control-plane
related:
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: ordering a source's flows, and retiring one without deleting it

**The problem.** A source is not one flow. It is an acquisition, several pre-transforms, and several
ODS loads that must run in that order, and it has to be possible to turn the whole thing off, or
turn one flow off, without losing what it was.

## One schedule per source, on the anchor

The estate's convention is a single schedule defined on the acquisition anchor, which every
downstream flow of that source then joins by name:

```yaml
# in the acquisition flow
schedule:
  name: vendor_daily
  cron: "0 4 * * *"
  timezone: Europe/Oslo
  catchup: false
  enabled: true
```

```yaml
# in every pre and ods flow of the same source
schedule: vendor_daily
```

One fire then runs the whole source in dependency order. The ordering comes from **lineage**, not
from the schedule and not from `batch`.

**`batch` is a grouping label with no scheduling effect.** It groups a dataset's flows for filtering
and reporting. Using it as if it controlled order is a common misreading; the convention is
per-dataset (`trips`, `stations`), while the acquisition flow's batch is its flow type.

## Waves and chaining

Within one fire, flows run in waves derived from the lineage graph: everything with no unmet
dependency runs, then the next layer. A cycle does not deadlock; the members are scheduled together
in a final fallback wave with the cycle reported.

`schedule.after` chains one schedule behind another, which is how a source with genuinely separate
stages (five regional loads that must not run concurrently) serialises them. `apc/` defines six named
schedules chained this way.

`catchup` decides whether a missed fire is made up. Leave it off for a source that always reads a
rolling window, since catching up re-reads the same data; turn it on where each fire covers a
distinct slice that would otherwise be skipped.

**Schedule a parallel run after the legacy wave, never mirroring its trigger.** While both systems
run, firing them together produces a shortfall above the new system's watermark that looks like data
loss and is only a race.

**A disabled schedule still needs a cron.** `enabled: false` with no cron expression is dropped by
the catalog sync entirely, so the schedule does not exist to re-enable later.

## Retiring a flow without deleting it

`mode` is the envelope-level switch, and it is used heavily: 99 production documents are `disabled`.

| Mode | Runs on a schedule fire | Use |
| --- | --- | --- |
| `auto` (default) | yes | a working flow |
| `manual` | no | a working flow reserved for direct triggers |
| `disabled` | no | a retired source, or a run-once replay |

Deleting a retired flow deletes its lineage and its history. Disabling keeps both, with a visible
badge, and keeps the YAML as the record of what the source was. Naming a flow directly still runs it,
and a node-scoped run can opt into replaying non-auto descendants deliberately.

This is what makes a dead source archivable rather than lost: the replay flows stay in the repository
disabled, and the data they landed stays queryable.

## Cross-system steps

Not every step is an ingestion. The estate's non-ingestion flow kinds each cover a distinct need:

| Flow kind | Purpose | Exemplar |
| --- | --- | --- |
| `cpy` | copy files between stores, one flow with many `items` | 21 folders |
| `sftp` | pull a vendor drop, with `output` declared for lineage | `ferde/`, `bysykkelforhold/` |
| `sp` | run a stored procedure, with runtime `procedure.parameters` | `fara/`, `citybike/` |
| `inv` | trigger an external pipeline (ADF) via a service principal | `poweranalyze/` |
| `cal` | generate a calendar dimension | `calendar/` |
| `scm` | schema-level operations | `datanorge/` |

`sp` parameters take a `selectExp`, so a procedure argument is a runtime value computed at call time
rather than a constant baked into the YAML.

`inv` is how a step that SQLFlow does not own stays inside the same dependency graph: the ADF
pipeline becomes a node with `onErrorResume` deciding whether its failure stops the wave.

## Knowing who depends on the output

`subscribers` records the downstream consumers of a table (dashboards, reports, their owners and
their queries). It changes no behaviour. It exists so that the question "what breaks if this column
changes" has an answer in the repository rather than in someone's memory. The estate registers
roughly twenty consumers this way.

`output`/`outputs` on a file-moving flow declares what it produced, so lineage connects a copy or an
SFTP pull to the flows that read it. Without it, the graph has a hole exactly where the data enters.

## Production exemplars

| Flow folder | Pattern |
| --- | --- |
| `apc/` | six named schedules chained with `after` across five regions |
| `poweranalyze/` | `inv` triggering ADF under a declared service principal |
| `subscribers/` | the consumer registry for the whole estate |
| 21 folders | `mode: disabled` retiring flows while keeping lineage and history |
