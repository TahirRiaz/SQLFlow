---
id: flow-schedule
title: schedule section and the scheduler
type: flow-reference
summary: The named schedules a flow joins (a name, a list, or an inline cron or interval), shared and chained schedules in schedules.yaml, and how a fire runs them.
keywords:
  - schedule
  - cron
  - intervalseconds
  - timezone
  - enabled
  - catchup
  - maxconcurrency
  - scheduler service
  - paused
  - schedules.yaml
  - shared schedule
  - schedule reference
  - membership
  - chained schedule
  - after
  - fan-in
  - parentfreshnesshours
  - run now
  - backfill
  - operation
  - values
  - wave order
  - when does a source update
  - schedule plan
yamlPath: schedule
related:
  - concept-control-plane
  - cli-control-plane
  - cli-db
  - cli-worker
  - flow-overview
  - flow-batch
sourceRefs:
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Yaml/YamlScheduleLibraryLoader.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/FlowDocumentHeaders.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Core/ScheduleSpec.cs
  - src/SqlFlow.Core/Runs/ExecutionMode.cs
  - src/SqlFlow.Catalog/ScheduleClock.cs
  - src/SqlFlow.Catalog/ScheduleStore.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogDbContext.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/RunScopeExpander.cs
  - src/SqlFlow.Dispatch/DispatchState.cs
  - src/SqlFlow.ControlPlane/Background/SchedulerService.cs
  - src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
---

# schedule

The top-level `schedule:` key declares which named schedules a flow belongs to. It is written one of three ways: a schedule name (`schedule: nightly`), a list of names (`schedule: [nightly, hourly]`), or an inline block that declares a cadence on the flow itself (a cron expression evaluated in a time zone, or a fixed interval in seconds). A schedule owns a member set, the flows that joined it, and every fire runs that set: one run when it has one runnable member, one wave-ordered run group when it has several.

The key is parsed on every flow document kind, built-in (file flows and `flowType: ing`, `api`, `cpy`, `sftp`, `exp`, `trl`, `sp`, `inv`, `hc`, `cal`, `scm`, `batch`) and host-registered, because it is captured on the document envelope (`FlowDocument.Schedule` in src/SqlFlow.Yaml/YamlDocumentLoader.cs), not on any one flow model. Every kind that becomes a catalog pipeline schedules identically, source-control snapshots (`scm`) included: a snapshot has no place in the lineage graph, so when it shares a fire with other flows it runs in the fire's first wave. The one exception is `batch`, which declares no flow of its own and so projects no pipeline to join a schedule; a `schedule:` key on a batch document is ignored without a warning, and a batch is ordered by lineage and triggered explicitly. The sibling health-check pipeline that an ingestion document's `healthCheck:` block derives never inherits the document's schedule.

The key is purely declarative: the engine itself never schedules anything. A catalog sync (`sqlflow db sync`, or the control plane's managed repo sync) records each named schedule in the catalog together with the flows that joined it, and the control plane's scheduler service fires a schedule by enqueuing its members onto the same durable run queue a manual trigger uses.

```yaml
name: orders
schedule:
  cron: "0 6 * * *"
  timezone: "Europe/Oslo"
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_CONN_DWH}
  schema: dbo
  table: Orders
```

This inline block, which carries no `name:`, defines a schedule named `orders` whose only member is the flow `orders`.

## Keys

When `schedule:` is a name or a list of names, the flow joins those schedules and carries no keys of its own. When it is a mapping, it takes these keys:

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `cron` | string | one of `cron` / `intervalSeconds` | none | Cron expression: 5 fields (minute granularity) or 6 fields with a leading seconds field. Evaluated in `timezone`. |
| `intervalSeconds` | int | one of `cron` / `intervalSeconds` | none | Fixed number of seconds between fires. Must be positive. |
| `timezone` | string | no | `"UTC"` | IANA time zone id the cron expression is evaluated in, for example `Europe/Oslo`. Ignored for interval schedules. |
| `enabled` | bool | no | `true` | Whether the schedule fires on its own. A disabled schedule is recorded in the catalog with its members but is never fired by the clock or by a chain; it still runs when fired on demand through the run-now endpoint. |
| `catchup` | bool | no | `false` | Whether missed occurrences are replayed. With `false` an overdue schedule fires once when the scheduler next sees it and its next fire is computed strictly after now, so any further missed occurrences are dropped. With `true` the next fire is computed from the occurrence just fired, so every missed occurrence fires, one per scheduler tick, until the schedule is current. |
| `maxConcurrency` | int | no | `4` | How many of the schedule's members execute at the same time, when a fire has more than one. Because waves are gated, this is the width of the running wave. `1` runs the fire strictly serially; `0` opts out and runs the wave unbounded. See [Fire width](#fire-width-maxconcurrency). |
| `name` | string | no | the declaring flow's name | The schedule's name. Other flows join this schedule by writing `schedule: <name>`, and every fire then runs the declaring flow and every flow that joined as one wave-ordered group. See [Shared schedules and membership](#shared-schedules-and-membership). |
| `operation` | string | no | none | The operation each fire runs on the members whose registered flow kind declares it; left out, each member runs its kind's default operation. 1 to 32 characters of lowercase letters, digits and `-`, starting with a letter. See [operation and values](#operation-and-values). |
| `values` | map of string to string | no | none | Parameter values each fire passes to the members of a registered flow kind. At most 32 entries; each name is an identifier of at most 64 characters, each value at most 1000 characters without control characters. |

Exactly one of `cron` or `intervalSeconds` must be set for an inline block to declare a schedule. A block that sets neither is treated as absent: the loader parses it to no schedule at all (src/SqlFlow.Yaml/YamlDocumentLoader.cs, `MapSchedule`), so an empty block is never stored as a broken schedule. An inline block cannot chain: it has no `after` key, so chaining is declared in a schedule library (see [Chained schedules](#chained-schedules-after)).

There is no `scope` key: what a fire runs is the schedule's member set. A `scope:` key left in an old document is ignored without a warning.

## Fire width: `maxConcurrency`

When a fire has more than one runnable member, it enqueues them as one wave-gated run group: no member can start until every member of a lower wave is terminal, so exactly one wave is eligible at a time and the members of that wave are free to run together. `maxConcurrency` bounds how many of them actually do. A fire with one runnable member enqueues a single run, and the bound does not apply.

```yaml
schedules:
  sales_daily:
    cron: "3 7 * * *"
    timezone: Europe/Oslo
    maxConcurrency: 4     # at most 4 of the source's flows execute at once
```

**Why it belongs to the schedule.** The members of a wave almost always share one upstream, and that upstream is
usually the scarce resource: 23 flows reading a single modest SQL Server can exhaust its connection budget while the
estate still has plenty of worker capacity. The alternative lever, a node's `ControlPlane:Worker:MaxConcurrentRuns`,
limits every run that node executes, so lowering it to protect one fragile source throttles every other source on that
node too. The bound belongs to the thing that fans out.

**Values.**

- Omitted takes the product default of **4** (`ScheduleDefaults.MaxConcurrency`). This is deliberately bounded rather
  than unbounded: a schedule nobody has tuned should not be able to open an unlimited number of connections against
  one server.
- `1` makes the fire strictly serial, one member after another.
- `0` is the explicit opt-out: the wave runs unbounded, limited only by worker capacity.
- A negative value is not a usable bound and falls back to the default: the schedule-library loader reports it as a
  warning, while an inline block and the API apply the default silently.

**How it is enforced.** The schedule's bound is stamped onto each member run at enqueue
(`CatalogRun.GroupMaxConcurrency`, copied from `CatalogSchedule.MaxConcurrency`). The control plane's dispatcher does
not hand out a member while that many members of its group are already reserved or running
(src/SqlFlow.Dispatch/DispatchState.cs). Because the bound travels on the run rows, a group already in flight keeps the
bound it was queued under: editing the schedule never retunes a wave that is already running.

**Precision.** The bound is exact across the fleet: one dispatcher is active per estate, it makes every hand-out
decision under a single lock, and a member counts against its group's bound from the moment it is reserved for a node.

**Existing schedules.** The catalog column is null (unbounded) for schedules stored before this key existed. A
git-declared schedule takes the default on the next sync, which rewrites its row; an API-created schedule keeps the
null bound, because a sync never touches API schedules.

## Membership: what a fire runs

A fire runs the schedule's member set: the flow that declares an inline block, plus every flow that joins the schedule by name. A whole data source refreshes together by having each of its flows join one schedule, usually defined in a schedules.yaml library:

```yaml
# sales.schedules.yaml
schedules:
  sales_daily: { cron: "0 4 * * *", timezone: "Europe/Oslo" }
```

```yaml
# each of the source's flows (copy, landing, ingestion) writes:
schedule: sales_daily
```

At fire time the members are read from the catalog (src/SqlFlow.Catalog/RunScopeExpander.cs, `ExpandScheduleAsync`) and ordered by each pipeline's lineage wave, renumbered from 0 over the members that actually run. Several members are enqueued as one wave-gated run group, and the control plane's dispatcher hands a member to a node only once every member in a lower wave is terminal (src/SqlFlow.Dispatch/DispatchState.cs), which gives these guarantees:

- **Waves run in order.** Wave N+1 starts only after every flow in wave N has finished, so a flow never runs before what it depends on.
- **A wave runs concurrently.** Members sharing a wave have no gate against each other and run together, up to `maxConcurrency`.
- **A failure stops what depends on it.** When a member fails, every still-queued member that transitively depends on it is marked `skipped` with the reason `skipped: an upstream dependency ('<flow>') did not succeed.`; independent members continue.
- **A flow never overlaps itself.** A run never starts while another run of the same flow, outside its own group, is executing.

Lineage never adds a flow to a fire: every flow that should run must join the schedule. A flow may join several schedules, and each of them runs it. The estate scan warns about every `mode: auto` flow that joins no schedule: `'<flow>' (<file>) is attached to no schedule and is not 'mode: manual' or 'mode: disabled', so nothing will ever run it; join one with 'schedule: <name>'.`

A schedule fire, automatic or on demand, never includes an inactive, `mode: manual` or `mode: disabled` member: a manual flow reserved itself for a direct trigger, and a disabled one is deactivated. Both remain directly runnable. A schedule whose member set resolves to nothing runnable enqueues nothing and logs `Schedule {ScheduleId} ('{ScheduleName}') resolved to no runnable flow: nothing joins it, or every member is deactivated or mode: manual. Nothing enqueued this occurrence.`; a run-now request answers 409.

### Reading a schedule's plan

`GET /api/v1/schedules/{id}/plan` returns the cadence together with the resolved members in wave order, from the same expander the fire uses: `{scheduleId, repoId, name, cron, intervalSeconds, timezone, enabled, paused, nextFireUtc, lastFireUtc, anchor, memberCount, waveCount, members: [{flowName, flowKind, wave, batch, pipelineId}]}`. `anchor` is the schedule name, waves start at 0, and `batch` is `default` for a flow that declares none. A chained schedule's plan has a null `nextFireUtc`; its parents are on the schedule itself (`afterSchedules`). It is the authoritative answer to "when does this source next update, and what runs in which order", and is exposed to the MCP server as `get_schedule_plan`.

### Reading the YAML behind a schedule

`GET /api/v1/schedules/{id}/definition` returns where git declares the schedule and the text of that declaration, `{scheduleId, name, source, path, flowName, pipelineId, yaml}`, so a schedule can be read where it is operated instead of only in the repo. Every sync records the provenance on the schedule row (`DefinitionPath`, `DefinitionFlow`, `DefinitionYaml`):

- An inline `schedule:` block, named or not, resolves to its declaring flow: the response carries that flow's name, its pipeline id, its repo-relative path, and the document text as the catalog stores it (secret-redacted, the same copy the flow's own YAML view serves). When the declaring flow has left the estate, `yaml` and `pipelineId` are null and `path` falls back to the stored path.
- A `schedules.yaml` entry resolves to the library file, whose text is kept on the schedule row (nothing else in the catalog holds a library file); `flowName` and `pipelineId` are null.
- An API-created schedule has no file behind it, and answers with a null `path` and a null document rather than a reconstruction git does not contain.

The GUI's schedule list exposes this per row (the "View the YAML that defines this schedule" action).

### Whether the last fire worked

The schedule list and detail (`GET /api/v1/schedules`, `GET /api/v1/schedules/{id}`) carry `lastCounts`: the members of the last fire tallied by lifecycle state (`total`, `queued`, `running`, `succeeded`, `failed`, `cancelled`, `skipped`), or the single run's own state when the fire ran one flow, and `lastGroupActive`, true while the last group still has queued or running members. `lastCounts` is null when the schedule has never fired or its runs have aged out. The GUI rolls it up worst-wins into the status badge next to "Last run", the same rule a run group's header uses, so a set where one member failed reads as failed rather than merely "fired".

### cron

A standard cron expression parsed by Cronos. The field count decides the format (src/SqlFlow.Catalog/ScheduleClock.cs): five fields is standard minute granularity; six or more fields is parsed with a leading seconds field. The YAML loaders trim whitespace around the value.

Cron syntax is NOT validated at YAML parse time. The YAML layer has no scheduling-library dependency; validation happens where the schedule is armed:

- During a catalog sync, an invalid cron or time zone drops the whole named schedule with the warning `schedule '<name>' (<origin>) is invalid: <reason>`, where the origin is the library file or `'<flow>' (<file>)` for an inline block (src/SqlFlow.Catalog/CatalogSync.cs). Its member pipelines still sync, and a row stored for that schedule by an earlier sync is removed by the same sync. Chained schedules have no cadence and are not clock-validated.
- On the schedule API, `ScheduleClock.TryValidate` rejects the request with HTTP 400 before storage.

Cron evaluation is time-zone aware with correct daylight-saving handling. By default an overdue occurrence fires once and the next fire is computed strictly after now, so further missed occurrences are dropped; set `catchup: true` to replay every missed occurrence, one per scheduler tick, until current.

### intervalSeconds

A fixed interval: the next fire is now plus N seconds, or, with `catchup: true`, the occurrence just fired plus N seconds. The value must be positive. The validator counts a non-positive interval as unset (src/SqlFlow.Catalog/ScheduleClock.cs, `TryValidate`), so `intervalSeconds: 0` on its own fails with `a schedule must set exactly one of 'cron' or 'intervalSeconds'.`; a non-positive interval alongside a `cron` fails with `'intervalSeconds' must be a positive number of seconds.` `timezone` has no effect on interval schedules.

Setting both `cron` and `intervalSeconds`, or (via the API) neither, fails validation with `a schedule must set exactly one of 'cron' or 'intervalSeconds'.`

### timezone

An IANA time zone id such as `Europe/Oslo`, resolved through `TimeZoneInfo.FindSystemTimeZoneById`. Blank or `UTC` (case-insensitive) resolves to UTC; the YAML loaders and the API create path normalize a missing or blank value to `"UTC"`. An unknown time zone makes a cron schedule invalid: the catalog sync drops it with a warning, and the API rejects it with 400. On an interval schedule the value is ignored and not validated (`ScheduleClock.TryValidate` only resolves the time zone when a cron is present).

### enabled

Defaults to `true`. `enabled: false` stores the schedule but the scheduler never fires it. It applies to the whole schedule, so it stops every member at once, and since a disabled schedule never fires on its own, the schedules chained behind it do not fire either. The run-now endpoint still fires a disabled schedule. This is the declarative flag from the YAML (or the API create body); it is distinct from the operational `paused` flag described below.

## Shared schedules and membership

Repeating the same `schedule:` block across every flow of a source is a maintenance trap: change the cadence and you must edit every file. Instead a schedule is defined once and joined by name from any number of flows in the same repo. A flow that references a schedule joins it: the cadence is not copied onto the flow, and the schedule fires once for all of its members. A flow joins a schedule by writing the `schedule:` value as the schedule's name, or joins several by writing a list of names:

```yaml
name: orders
schedule: nightly        # join the shared schedule named "nightly"
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_CONN_DWH}
  schema: dbo
  table: Orders
```

```yaml
schedule: [nightly, hourly]   # a member of both: each runs it
```

In a list, blanks are dropped and repeats collapse case-insensitively; an empty list or a blank name means no schedule.

Schedule names form one case-insensitive namespace per repo, filled from three sources:

1. **A dedicated `schedules.yaml` library file.** Any file named `schedules.yaml` or ending in `.schedules.yaml` (matched case-insensitively), anywhere in the repo, whose top-level `schedules:` key maps a name to a schedule. This is the natural home for a source's schedules (src/SqlFlow.Yaml/YamlScheduleLibraryLoader.cs):

   ```yaml
   # schedules.yaml
   schedules:
     nightly:   { cron: "0 6 * * *", timezone: "Europe/Oslo" }
     every-15m: { intervalSeconds: 900 }
     weekly:    { cron: "0 5 * * 1", timezone: "UTC", catchup: true }
   ```

   Each entry takes `cron` or `intervalSeconds` (or `after`), `timezone`, `enabled`, `catchup`, `maxConcurrency`, `operation` and `values` with the same meaning as on an inline block, plus `after` and `parentFreshnessHours` for chained schedules (see [Chained schedules](#chained-schedules-after)); the map key is the schedule's name. A library file is not a flow document (the estate scan reads every `*.yaml` file except schedule and subscriber libraries as a flow) and never becomes a pipeline. The loader drops an unusable entry with a warning, and `sqlflow validate` on a folder reports a library that raises any warning as broken:

   - `<file>: invalid schedule library - <message>`
   - `<file>: no 'schedules:' entries; nothing to publish.`
   - `<file>: a schedule with a blank name is ignored.`
   - `<file>: schedule '<name>' declares neither a cron, an intervalSeconds, nor an after; ignored.`
   - `<file>: schedule '<name>': <message> The schedule is ignored.` (an `operation` or `values` no run could accept)

2. **A named inline block on a flow.** An inline `schedule:` block may carry a `name:` key. The declaring flow and every flow that joins that name are members of one schedule, and each fire runs them together as one wave-ordered group:

   ```yaml
   # invoices.flow.yaml: defines "nightly" inline and is a member of it
   name: invoices
   schedule:
     name: nightly
     cron: "0 6 * * *"
     timezone: "Europe/Oslo"
   # ... orders.flow.yaml elsewhere just writes:  schedule: nightly
   ```

3. **An unnamed inline block,** which defines a schedule named after its declaring flow, so `schedule: orders` joins the unnamed inline schedule of flow `orders`.

Membership is resolved during the estate scan, where the whole repo is visible (src/SqlFlow.Lineage/Collection/FlowSetCollector.cs, `ResolveSchedules`): each schedule name is bound to its one definition and to the set of flows that joined it. Resolution rules:

- If a name is defined more than once, the first definition wins and the redefinition is warned: `schedule name '<name>' is declared more than once (<origin> redefines <first origin>); the first wins.` Library files are registered first, in path order, then inline blocks, so a library entry always wins over an inline block of the same name; the flow whose inline block lost still joins the winning schedule.
- A name that nothing defines is dropped from that flow's membership with the warning `'<flow>' (<file>) joins schedule '<name>', which no schedules.yaml or named inline block defines; the flow is left unscheduled.` The flow still joins any other names it lists.
- A library entry that no flow joins is still stored, with the warning `schedule '<name>' (<origin>) has no members: no flow joins it with 'schedule: <name>', so it would fire nothing.`
- Each named schedule is validated once, at sync, whichever way it was defined (see [cron](#cron)); a chained schedule has no cadence and is not clock-validated.

Schedules are written only by a full catalog sync, which sees the whole repo. The per-run write-back that records a single run and its flow's pipeline row (src/SqlFlow.Catalog/CatalogSync.cs, `RecordRunAsync`) never writes schedules, because membership is a repo-wide fact one flow file cannot establish.

## Chained schedules: `after`

A library entry may declare `after: <name>` (or a list, `after: [a, b, c]`) instead of a cadence, which makes it a chained schedule: it has no clock of its own and fires behind its parents. That is how a strict serial chain is expressed (`a` on a cron, `b: {after: a}`, `c: {after: b}`), which a stagger of independent crons can only approximate.

```yaml
schedules:
  land_daily:    { cron: "0 3 * * *", timezone: "UTC" }
  partner_daily: { cron: "30 2 * * *", timezone: "UTC" }
  model_daily:   { after: land_daily }                     # a serial link behind land_daily
  report_fact:   { after: [model_daily, partner_daily], parentFreshnessHours: 30 }   # a fan-in
```

- A chained schedule is stored with no cron, no interval and a null next fire, and is not clock-validated. An entry that sets `after` together with a cron or an interval keeps the chain and drops the cadence, with the warning `<file>: schedule '<name>' sets 'after: <names>' together with a cron/intervalSeconds; a chained schedule has no cadence of its own, so the cron is ignored.` An entry chained after itself is dropped: `<file>: schedule '<name>' chains after itself; ignored.` In a list, blanks are dropped and repeats collapse.
- Parents are resolved by name within the same repo. A chained schedule is ready when every declared parent exists, has fired, and has no queued or running run left from its last fire, and the oldest of those parent fires is newer than the one it last reacted to. Whether the parents succeeded is not considered: one failed link must not park every schedule behind it.
- With several parents (a fan-in) it waits for all of them, fires once per round of parent fires, and records the newest parent fire it reacted to. A chain advances one link per scheduler tick.
- A cycle, or a parent name nothing defines, never becomes ready and stalls without a warning. A disabled or paused link stops the chain there.
- `parentFreshnessHours` (default 24; `0` disables the check) is how recently every parent must have fired for the fire to count as fed by current data. A parent older than the window does not hold the fire back: the fire proceeds, the stale parents are recorded on the schedule (`lastStaleParents`) and logged as a warning. A negative value falls back to the default with the warning `<file>: schedule '<name>' sets parentFreshnessHours to <n>, which is not a usable window; using the default of 24 (use 0 to disable the check).`
- A parent fire that enqueued nothing (an empty member set, or a parked clock fire) still records its fire time, so the schedules chained behind it fire.
- A run-now fire records the fire time too, so by default the chain follows a manual fire; `chain=false` on the run-now request holds the direct children whose only parent is this schedule.

## operation and values

`operation` and `values` are run arguments for members of a registered flow kind (one a host module adds); SQLFlow's built-in kinds take none. They are accepted on inline blocks, library entries and API schedules:

- On an inline block they are validated against the declaring document's own kind when the document is parsed. A built-in kind refuses any, and the document then fails to parse with `<file>: schedule: operation, values and payload apply to flows of a registered kind; '<kind>' flows take none.`
- A library entry is shape-checked only (the operation pattern and the value limits above).
- An API schedule requires at least one member whose registered kind takes them, and every such kind validates them.

At fire time they are merged into the parameters of each member whose registered kind declares the operation (or of every registered-kind member when no operation is named); built-in members, and members of a kind without that operation, run as defined. Each member's kind validates the merged parameters, and a refusal fails the whole fire: an automatic fire is logged as `Schedule {ScheduleId} ('{ScheduleName}') failed to fire: {Error}` and its occurrence is not retried, and a run-now request answers 400 with nothing enqueued.

## From YAML to the catalog: sync semantics

A catalog sync (`sqlflow db sync <dir>`, the control plane's managed repo sync, or the catalog sync endpoint) writes one `Source = 'yaml'` schedule row per name, with an id derived from the repo and the lowercased name, plus that schedule's member rows and, for a chained schedule, its parent rows (src/SqlFlow.Catalog/CatalogSync.cs, src/SqlFlow.Catalog/ScheduleStore.cs, `StageYamlUpsertAsync`):

- The schedule is refreshed from the YAML on every sync; git is the source of truth for `yaml` schedules. `enabled`, `catchup`, `maxConcurrency`, `operation`, `values`, `parentFreshnessHours` and the definition provenance are refreshed each time.
- The member set is replaced from git on every sync: a flow that dropped its `schedule:` line stops being fired, and a new joiner starts. A member that a selection-scoped sync excluded is not stored as a member, and a pipeline that left the repo takes its memberships with it while the schedule itself survives.
- An operator's API pause survives a re-sync: `paused` is an operational override that a git refresh does not clear.
- Schedules created through the API (`Source = 'api'`) are never touched by sync. One unique index covers both sources, so a repo holds one schedule per name whichever way it was created: the API refuses a name git already uses (409), and a git schedule whose name an API schedule already holds (matched case-insensitively) is left out of the sync with the warning `schedule '<name>' (<origin>) is not synced: the repo already has an API-created schedule named '<name>' (id <id>), and a repo holds one schedule per name. Rename the one in git, or delete the API schedule, and sync again.` while everything else syncs. A sync and an API create take one application lock per repo before they read who holds a name (`ScheduleStore.LockScheduleNamesAsync`, owned by the transaction), so the check and the write are one step for each: an API create that arrives while a sync is under way waits for it and is then refused if the sync took the name, and a sync that starts while an API create is under way waits for it and then leaves the clashing git schedule out.
- A flow document still in the repo that does not load (the scan warns `<file>: is a flow document that does not load ...`) is held: its pipeline, the schedules its inline block declares and its memberships of other schedules stay as the last sync recorded them, with the warning `'<flow>' (<file>) does not load, so this sync holds its pipeline, schedules and schedule memberships as the last sync recorded them and leaves it out of lineage until the file is fixed.`, until the file loads again.
- A schedule removed from git is removed from the table, with its memberships, on the next sync.
- For a schedule name defined more than once, the first definition wins (the estate scan already warned).
- The next fire is computed from the sync time (falling back to the sync time itself when none is computable), but it only replaces the stored `NextFireUtc` for a new schedule, one whose cron/intervalSeconds/timezone or parent set changed, or one with no next fire; an unchanged re-sync, or a change of members alone, never disturbs the firing cadence. A chained schedule always has a null `NextFireUtc`, and a changed parent set also clears what it last reacted to.

## The scheduler service

`SchedulerService` (src/SqlFlow.ControlPlane/Background/SchedulerService.cs) is a control-plane background service that scans the catalog every `ControlPlane:Scheduler:PollSeconds` seconds (default 15, minimum 1; src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs) and on each tick fires at most 200 due clock schedules and at most 200 ready chained schedules, at most 8 at a time.

A clock schedule fires when it is `Enabled`, not `Paused`, and its `NextFireUtc` has arrived; a chained schedule fires when it is `Enabled`, not `Paused`, and ready (see [Chained schedules](#chained-schedules-after)). Firing:

1. Computes the next occurrence: strictly after now by default (the overdue occurrence fires once and any others are dropped), or, when `catchup` is set, strictly after the occurrence just fired (so an overdue schedule advances one occurrence per tick, firing each missed window until it is current).
2. Claims the occurrence with a compare-and-swap on `NextFireUtc`, so multiple control-plane nodes never double-fire the same occurrence. The winning claim also stamps `LastFireUtc`.
3. Expands the member set, leaving out members that are inactive, `mode: manual` or `mode: disabled`. When nothing runnable remains it enqueues nothing and logs the "resolved to no runnable flow" line above; the next fire has already advanced.
4. Enqueues the members onto the same durable queue a manual trigger uses: one member as a single run (recorded as `LastRunId`), several as one wave-gated run group carrying the schedule's `maxConcurrency` (recorded as `LastGroupId`, with the first member as `LastRunId`). The schedule's `operation` and `values` reach the members as described in [operation and values](#operation-and-values). Each run records the trigger source `schedule`, the schedule id, and the requester `schedule:<name>`.

A malformed cron, an unknown time zone, or a cron with no future occurrence has no computable next fire: the claim advances `NextFireUtc` to null and the schedule is parked (logged: `... has no computable next fire (invalid cron/timezone or exhausted) and was parked.`); the occurrence that was due is not enqueued. A tick error such as a transient database outage is logged and retried on the next tick, and a failure firing one schedule is logged without stopping the others.

Scheduled runs are enqueued untargeted (the dispatcher hands them to any node serving the untargeted pool) with no explicit commit pin on the request; enqueueing itself pins the run to the repo's last successfully synced commit when one is known, and snapshots the pipeline's YAML so the node can run it without git (src/SqlFlow.Catalog/RunQueueStore.cs).

## The schedule API

The control plane also manages schedules directly (src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs). All routes are mounted under `/api/v1`. Reads are mapped under the `read` policy and mutations under `operate`; both admit any signed-in caller, since only user administration checks a scope (see [authentication and identity](../concepts/authentication-and-identity.md)). The CLI drives the same routes with `sqlflow schedules list|show|create|run|pause|resume|delete` (see [control-plane verbs](../cli/control-plane.md)).

| Endpoint | Description |
| --- | --- |
| `GET /schedules` | List, ordered by name, filterable by `repoId`, `pipelineId` (schedules the flow is a member of), `source` (`yaml` or `api`), `enabled`, and `search` (a substring of the schedule name); paged with `page` and `pageSize` (default 50, maximum 200). |
| `GET /schedules/{id}` | One schedule; 404 when unknown. |
| `GET /schedules/{id}/plan` | The resolved members in wave order (see [Reading a schedule's plan](#reading-a-schedules-plan)); 404 when unknown. |
| `GET /schedules/{id}/definition` | The YAML behind the schedule (see [Reading the YAML behind a schedule](#reading-the-yaml-behind-a-schedule)); 404 when unknown. |
| `POST /schedules` | Create an `api` schedule from `{repoId, members: [flow names], cron or intervalSeconds, timezone?, enabled?, catchup?, name?, maxConcurrency?, operation?, values?}`; `name` defaults to the first member, and an API schedule cannot chain. Answers 201 with `{id, nextFireUtc}`. 400 when `members` is empty, on invalid timing, or when `operation`/`values` are invalid or no member's registered kind takes them; 404 naming every member that is not an active pipeline; 409 when the repo already has a schedule with that name (checked and inserted as one step under the repo's schedule-name lock, which a sync takes too); 503 when another writer of the repo's schedules held that lock for two minutes (nothing was written; try again). |
| `POST /schedules/{id}/run` | Fire the schedule now: enqueues its member set through the same path a scheduled fire uses, records the run (or group) and the fire time on the schedule, and leaves the next scheduled fire unchanged. It also fires disabled and paused schedules, and records the caller as the requester. Optional query parameters: `batch` (repeatable) narrows the fire to members whose `batch:` matches one of the values (a flow without one matches `default`); `from` and `to` make the fire a backfill (below); `chain=false` keeps the schedules chained directly behind this one from following. 202 with `{runId, groupId, memberCount}`; 400 for an invalid backfill window or when a member's kind refuses the schedule's operation or values; 404 when unknown; 409 when no member is runnable (or none carries the requested batches). |
| `POST /schedules/{id}/pause` | Set the operational `paused` flag; the next fire is left as it was. 200 with the schedule; 404 when unknown. |
| `POST /schedules/{id}/resume` | Clear `paused` and recompute the next fire from now, so a long pause never releases a burst of missed fires. A schedule with no computable next fire (a chained or a parked one) keeps none. 200 with the schedule; 404 when unknown. |
| `DELETE /schedules/{id}` | Remove the schedule and its memberships; 204 on success, 404 when unknown. Deleting a `yaml` schedule is undone by the next sync while git still declares it. |

A run-now backfill (`from`, optionally `to`; `to` requires `from` and must be after it) reprocesses the source for that range by each member's role: the members of the fire's first wave whose kind is `cpy`, `api`, `sftp` or `file` take the window and re-land their files, every `ing` member re-reads from the source minimum so the re-landed rows are picked up, and every other member runs with default parameters.

`ScheduleDto` fields: `id`, `repoId`, `name`, `memberPipelineIds`, `cron`, `intervalSeconds`, `timezone`, `enabled`, `catchup`, `paused`, `source`, `nextFireUtc`, `lastFireUtc`, `lastRunId`, `lastGroupId`, `lastGroupActive`, `createdUtc`, `updatedUtc`, `maxConcurrency` (null means unbounded), `lastCounts`, `afterSchedules` (the parents, in declaration order), `triggersSchedules` (the schedules this one sets off, each `{id, name, depth, memberCount, enabled, paused}`; null when none), `parentFreshnessHours`, `lastStaleParents`, `operation`, `values`.

## Examples

An interval schedule, declared but not yet active (a single-member schedule named `orders`):

```yaml
name: orders
schedule:
  intervalSeconds: 900
  enabled: false
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_CONN_DWH}
  schema: dbo
  table: Orders
```

A second-granularity cron on an ingestion flow (6 fields, leading seconds field), fired every 30 seconds during business hours in Oslo:

```yaml
flowType: ing
name: staging-orders
schedule:
  cron: "*/30 * 8-17 * * MON-FRI"
  timezone: "Europe/Oslo"
connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}
source:
  server: erp
  object: AdventureWorks.Sales.Orders
target:
  server: dwh
  object: DW.raw.Orders
load:
  keyColumns: [OrderID]
```

Arm the schedules by syncing the repo into the catalog:

```bash
sqlflow db sync . --repo my-estate
```

## See also

- [Flow document overview](./overview.md)
- [Batch flows](./batch.md)
- [The control plane](../concepts/control-plane.md)
- [Control-plane verbs](../cli/control-plane.md)
- [worker command](../cli/worker.md)
