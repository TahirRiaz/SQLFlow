---
id: concept-what-started-a-run
title: "What started a run: how every run records it, and where it shows"
type: concept
summary: "Every run records what started it: a schedule on its clock, a schedule behind its parents' fire (with the fire it followed), a person or client, an external trigger, or the command line. The API, the GUI and the CLI say it the same way."
keywords:
  - started by
  - what started a run
  - who started a run
  - requested by
  - trigger source
  - chained fire
  - run now
  - external trigger
  - RunStartedBy
  - RunStartedByDto
  - TriggerRunId
  - causes
related:
  - concept-control-plane
  - flow-schedule
  - concept-external-triggers
sourceRefs:
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/ScheduleStore.cs
  - src/SqlFlow.Catalog/Migrations/20261113120537_RunTriggerCause.cs
  - src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
  - src/SqlFlow.ControlPlane/Background/SchedulerService.cs
  - src/SqlFlow.ControlPlane/Api/RunStartedBy.cs
  - src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.cs
  - gui/src/components/RunStartedBy.tsx
  - gui/src/features/runs/startedBy.ts
  - gui/src/features/runs/RunsPage.tsx
---

# What started a run

A run that nobody expected raises one question first: what, or who, started it. Every run answers it from what it
recorded when it was queued, and the control plane words the answer once (`RunStartedBy`), so the runs board, a run's
page, a run group, the schedule timeline, the CLI and the MCP server all say the same thing.

## What a run records

Every path that queues a run records how it started on the run row, in the same write that queues it:

| Column | What it holds |
| --- | --- |
| `TriggerSource` | `schedule`: a schedule fired on its own clock (its cron or interval). `chain`: a schedule fired on its own behind its parents' fire (`after:`). `manual`: a person or a client asked. `cli`: a local `sqlflow run` recorded from its run file. Null on runs recorded before the column existed. |
| `RequestedBy` | Who asked: the caller's name (the token's subject), `trigger:<name>` for an external trigger, `schedule:<name>` for a schedule's own fire, `cli:<user>@<machine>` for a command-line account. |
| `TriggerScheduleId` | The schedule whose member set the run is one of, whether it fired on its own or someone ran it. |
| `TriggerRunId` | For a chained fire: the first run of the parent fire it followed, when that fire queued any. |

How each way of starting a run is recorded:

| Started by | `TriggerSource` | `RequestedBy` | Links |
| --- | --- | --- | --- |
| A schedule's clock | `schedule` | `schedule:<name>` | the schedule |
| A schedule behind its parents | `chain` | `schedule:<name>` | the schedule, the parent fire's run |
| Run now on a schedule (GUI, API, `sqlflow schedules run`) | `manual` | the caller | the schedule |
| An external trigger's request | `manual` | `trigger:<name>` | the schedule, the request |
| Run, Re-run or Run batch in the GUI, `POST /runs`, `sqlflow trigger`, an MCP tool, a module's operator action | `manual` | the caller | none |
| A local `sqlflow run`, synced in | `cli` | the account, when the run file names one | none |

A caller whose credential carries no name is recorded as `unknown`, never as the schedule, so a run someone asked for
never reads as one the estate ran on its own. A fan-out member records what its root recorded. A chained fire whose
newest parent fire queued nothing (every member deactivated) records no parent run; it still records `chain`.

Runs queued before the `RunTriggerCause` migration recorded a schedule's run-now and an external trigger's fire as
`schedule`. The migration records those runs as `manual`, told apart exactly by `RequestedBy`, which names the person or
the trigger where a schedule's own fire names the schedule. Chained fires from before it read as the schedule's own fire,
and have no parent run to link.

## The answer: `startedBy`

`RunStartedBy.Describe` turns what a run recorded into one answer, `RunStartedByDto`:

| Field | Meaning |
| --- | --- |
| `kind` | `schedule`, `chain`, `user`, `trigger`, `cli` or `unknown` (below). |
| `summary` | The answer in one sentence. |
| `who` | The person, client, external trigger or command-line account that asked; null when the estate ran it itself. |
| `scheduleId`, `scheduleName` | The schedule whose member set the run is one of. |
| `afterRunId`, `afterScheduleName`, `afterFlowName` | For `chain`: the parent fire's first run, its schedule and its flow. |
| `triggerRequestId` | For `trigger`: the external trigger's request. |

| Kind | Example `summary` |
| --- | --- |
| `schedule` | `Schedule nightly fired on its own.` |
| `chain` | `Schedule reference-data fired after schedule land-files finished.` |
| `user` | `alice ran schedule nightly now.`, or `alice asked for this run.` |
| `trigger` | `External trigger merge-to-main fired schedule nightly.` |
| `cli` | `Run from the command line by tahir@build-01.` |
| `unknown` | `Not recorded: the run predates the record of what starts a run.` |

Who asked decides over the trigger source: a run that names a person or a trigger was asked for, whatever source it was
recorded with, and one that names its schedule was the schedule's own fire. A schedule whose row has left the catalog is
named from `schedule:<name>`.

## In the API

- `GET /runs` carries `startedBy` on every summary, with `triggerScheduleId` and `triggerRunId`, read for the whole page
  in at most three queries. `startedBy=<kind>` keeps only the runs of that kind, by the same rules each row's label
  follows; any other value answers 400. The group stream's member frames leave `startedBy` null: every member of a group
  shares the one the group carries.
- `GET /runs/{runId}` carries `startedBy` and `causes`: for a chained run, the chain of parent fires behind it, from the
  fire it followed back to the first link (at most 16), each with its run, flow, group, status and `startedBy`; empty for
  every other run.
- `GET /runs/groups/{groupId}` carries the group's `startedBy`.

## In the GUI

- The runs board has a **Started by** column (the schedule, person, trigger or account, how, and the whole sentence on
  hover) and a **Started by** filter. `/runs?startedBy=user` opens it filtered.
- A run's page and a run group's page say what started them under their title, with links to the schedule's runs, the
  parent fire's run and the external trigger's request. A chained run's page lists the chain of fires behind it, back to
  the person or clock that set it off.
- The schedule timeline's bar hover says what started each run: a lane holds every run of its schedule's flows, not only
  the schedule's own fires.
- The Schedules page names the schedules a chained schedule fires behind, and shows a cron whose date never comes as a
  next fire of **never**.

## In the CLI

- `sqlflow runs list` ends each row with STARTED BY, and `--started-by <kind>` keeps the runs of that kind.
- `sqlflow runs show` prints `started by:` with the sentence, then an `after:` line for each link of a chained run's
  chain. `sqlflow groups show` prints the group's `started by:`.
