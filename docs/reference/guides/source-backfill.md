---
id: guide-source-backfill
title: Backfilling a whole source (integration, file ingestion, silver)
type: guide
summary: "Reprocess a whole ingest source for a date range: integration re-lands files, file ingestion re-reads them, silver re-pulls from the source minimum."
keywords:
  - backfill
  - reprocess
  - whole source
  - schedule backfill
  - force re-land
  - min from source
  - reprocessFromSourceMin
  - flow + descendants
  - integration
  - silver
related:
  - guide-incremental-and-backfill
  - flow-incremental
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.Core/Runs/RunParameters.cs
  - src/SqlFlow.Core/Runs/RunParameterApplicability.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.Copy/CopyEngine.cs
  - src/SqlFlow.Acquire/Engine/AcquireFlowRunner.cs
  - src/SqlFlow.Acquire/Engine/LandingPipeline.cs
  - src/SqlFlow.Sftp/SftpEngine.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
---

# Backfilling a whole source

A single-flow backfill (`--full`, `--from`/`--to` on one flow) is covered by
[Incremental loads and per-run backfill parameters](incremental-and-backfill.md). This guide is about the harder
case: reprocessing an **entire ingest source** for a date range, across all the flows that move its data, in one
operation. That is what you want when historical data needs to be re-pulled all the way from the external system
into the silver tables, not just re-read at one hop.

The backfill is valid for the three roles of an ingest source:

1. **Integration** flows that fetch from an external system: copy (`cpy`), API acquire (`api`), and SFTP (`sftp`).
2. **File ingestion** flows that load the fetched files into the database (`file`), landing them in the `pre` layer.
3. **Silver** flows that load the `pre` layer into the durable tables (`ing`, a relational ingestion).

A typical source looks like `copy -> file -> ing` (wave 1 -> wave 2 -> wave 3). Every other flow kind (export,
stored procedure, health check, inventory, and so on) is not part of a backfill and runs exactly as defined.

## The problem it solves

Incremental flows keep no control database. The **target table is the state**: each run probes the target for a
watermark and reads only what is newer. That is efficient, but it makes a naive backfill fail in two ways:

1. **The integration layer skips re-landing.** A copy/acquire/sftp flow deduplicates: a file whose content already
   exists at the destination is not re-written, so its timestamp is not bumped and nothing downstream re-triggers.
   Re-running it transfers nothing.
2. **The silver layer filters the backfill out.** Even if back-dated rows reach the `pre` layer, the next flow reads
   `WHERE date_col > MAX(target)`. Back-dated rows are below that high-water mark, so they are never picked up.

A whole-source backfill fixes both, per role, in one run.

## What each role does during a backfill

| Role | Kinds | What the backfill does |
|---|---|---|
| Integration | `cpy`, `api`, `sftp` | Selects source files by **modified date** in the from/to window and **force re-lands** them: unchanged-detection is turned off, so every selected file is re-written with a **fresh timestamp**, even when byte-identical. Acquire additionally **ignores its stored watermark** so it re-fetches history rather than staying capped at the last point. |
| File ingestion | `file` | A **root** file flow (one that reads external files directly) takes the window and reads the files whose date falls in it, with its watermark suppressed. An **intermediate** file flow (one that reads a root's re-landed output) runs at defaults: the re-landed files are the newest at its source, so its own normal incremental picks them up. |
| Silver | `ing` | Reads `WHERE date_col >= MIN(source)` instead of `> MAX(target)`, so the back-dated rows the file layer just re-landed are re-pulled and upserted. |

Two mechanisms sit underneath that table:

- **Force re-land** (integration): a backfill is an explicit "reprocess these files" request, so the copy/acquire/sftp
  engine disables its unchanged-file skip for the run. The files re-enter the pipeline with current timestamps. See
  `CopyEngine`, `LandingPipeline`, `SftpEngine`.
- **Min-from-source** (silver): the relational resolver probes `MIN(source)` in addition to `MAX(target)` and bounds
  the read at the minimum. For an **explicit** operator backfill this is **unconditional**: it always reads from the
  source minimum, even when the source holds nothing older than the target, so the reprocess is never silently
  narrowed back to the target's high-water mark. See `IncrementalWindowResolver`.

### Why the window lives only at the integration layer

The from/to window is a **modified-date** selection, and a modified date only exists meaningfully at the external
source. Once a file is re-landed it carries a fresh timestamp, so an old date window can no longer select it. That is
why the window is applied at the integration roots and **not** re-applied downstream: the file layer re-reads what
re-landed through its normal incremental, and the silver layer re-pulls by source minimum. Trying to re-enforce the
same past window on a freshly re-landed file would select nothing.

## How to run it

### Flow + descendants (a sub-tree of one source)

In the GUI, open **Trigger run**, choose **This flow + descendants**, pick the source's entry flow (its copy or
acquire), and set the **Backfill window** From/To. The anchor takes the window and force-re-lands; its descendants are
routed automatically (silver descendants to min-from-source, intermediate file/copy descendants to defaults).

The same over the API (`POST /api/v1/runs`):

```jsonc
{
  "repoId": "…",
  "flowName": "boatbooking_00_cpy",
  "scope": "node",
  "backfillFrom": "2026-07-07T00:00:00Z",
  "backfillTo":   "2026-07-16T00:00:00Z"
}
```

### The whole source (a schedule fire)

"Whole batch" is not a run scope in SQLFlow; a whole source is expressed as a **schedule** (the flows that joined it
with `schedule: <name>`). To backfill the entire source, open its schedule's **Run schedule** board, set the
**Backfill window**, and start. Firing the schedule with a window reprocesses every member in wave order: its wave-0
integration/file roots take the window, its silver flows take min-from-source, everything else runs at defaults.

The same over the API (`POST /api/v1/schedules/{id}/run`):

```
POST /api/v1/schedules/{id}/run?from=2026-07-07T00:00:00Z&to=2026-07-16T00:00:00Z
```

The optional `batch=<tag>` filter still applies and can be combined with the window to backfill one batch of the
source. The next scheduled fire is not moved: an on-demand backfill never touches the cadence.

### The CLI

A single-flow or flow+descendants run takes `--from`/`--to` on `sqlflow run` (see the
[single-flow guide](incremental-and-backfill.md)). A schedule-level backfill is triggered through the control-plane
schedule endpoint above.

## What you should see in the run trace

On the **integration** flow (copy shown):

```
copy.backfill  backfill run: unchanged-detection disabled, so every file in the window re-lands (overwritten even if unchanged).
copy.list      matched 16 file(s) … (modified 2026-07-07 .. 2026-07-16).
copy.write     copied … -> '…'          (not "copy.skip … (no change)")
```

On a **silver** flow, the watermark source flips from the target to the source minimum:

```
incremental.window   run parameters applied: reprocess from source min
incremental.window   incremental read: WHERE 1=1 AND [FileDate_DW] >= 20260707000000
incremental.min-probe SELECT MIN([FileDate_DW]) … FROM [pre].[v_…]
```

and the run detail shows **`WATERMARK SOURCE: source MIN [pre].[…]`** rather than `target MAX [arc].[…]`. If a silver
run still shows `target MAX`, it was executed by a worker that does not yet have this behavior (see Operational notes).

## Semantics and defaults

- The window is `>= from` and `< to` at the integration layer's modified-date filter; an open-ended `from` with no
  `to` is a legitimate "everything modified since" backfill.
- `ReprocessFiles` (the internal flag that turns off unchanged-detection and bypasses the acquire watermark) is
  `FullLoad || BackfillFrom is set`. So a full load also force-re-lands.
- Min-from-source and a backfill window are mutually exclusive on one flow: an anchor carries the window, a silver
  descendant carries the reprocess flag, never both.
- Everything is a per-run override. Nothing is written to the flow's YAML; the backfill is an audited operational act
  recorded on the run, and the next normal run behaves exactly as defined.

## Flow-kind reference

| Kind | Meaning | In a backfill |
|---|---|---|
| `cpy` | Copy files between locations | Window + force re-land |
| `api` | Acquire from an API | Window + force re-land + stored-watermark bypass |
| `sftp` | SFTP transfer | Modified-date window + force re-land |
| `file` | Load files into the database | Root: window (file-date, watermark suppressed). Intermediate: default |
| `ing` | Relational (silver) load | Min-from-source (unconditional for an explicit backfill) |
| `exp` `sp` `hc` `inv` `scm` `batch` | Export, stored proc, health check, inventory, source-control, batch document | Not part of a backfill; run as defined |

## Edge cases

- **Batch filter that excludes the integration root.** Firing `?batch=<silver-only>&from=…&to=…` puts no root fetch
  flow in the fired set, so nothing re-lands; the silver flows still re-pull from the source minimum of whatever is
  already in `pre`. The window has no re-fetch effect in that case.
- **A standalone silver flow.** A single relational flow reached through a one-member schedule is routed to
  min-from-source and ignores the window. To bound a standalone relational backfill by its date column, trigger it in
  **flow** scope, where the window is applied to the date column directly.
- **File-date source.** A root `file` flow's window filters by the file's business date only when the flow declares a
  `fileDate` (path/name) spec; without one it filters the file's modified timestamp. This is a flow-definition choice,
  not a routing choice.
- **Cursor-based API flows.** An `api` flow whose incremental is a bind-variable cursor (not a date window) has its
  watermark bypassed by a backfill, so it re-fetches from its seed. A date window cannot bound a cursor fetch, so the
  effect is a full reprocess rather than a date-bounded one.

## Operational notes

- **Every worker must run the same build.** A silver flow only reads from the source minimum on a worker that has this
  behavior. If a fleet is mid-deploy (some workers old), a run claimed by an old worker falls back to `target MAX`.
  Deploy the whole fleet before relying on the behavior in an estate that shares its run queue.
- A whole-source backfill fans a `>= source min` read across every silver flow in the source, so it is heavier than a
  single node run. It is idempotent (keyed upserts), but confirm the intent before firing a whole source.
