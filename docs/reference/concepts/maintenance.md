---
id: concept-maintenance
title: "Maintenance: what the catalog's database holds, what is kept, and the safe purge"
type: concept
summary: "Every table of the catalog's database in storage areas, each with a verdict (keep, safe to remove, optional) and its size; admins set limits and clean up."
keywords:
  - maintenance
  - storage
  - retention
  - cleanup
  - storage area
  - safe to remove
  - optional
  - keep
  - purge
  - clean up
  - run statements
  - run events
  - IStorageContributor
  - ControlPlane:Maintenance
related:
  - concept-shadow-catalog
  - concept-control-plane
  - guide-notifications
  - concept-run-artifacts
  - concept-external-triggers
sourceRefs:
  - src/SqlFlow.ControlPlane/Maintenance/StorageArea.cs
  - src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - src/SqlFlow.ControlPlane/Maintenance/MaintenanceRunner.cs
  - src/SqlFlow.ControlPlane/Maintenance/MaintenanceReaper.cs
  - src/SqlFlow.ControlPlane/Maintenance/StorageReport.cs
  - src/SqlFlow.ControlPlane/Api/MaintenanceEndpoints.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneModuleServices.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.Catalog/MaintenanceStore.cs
  - src/SqlFlow.Catalog/CatalogStorage.cs
  - src/SqlFlow.Catalog/RunTraceStore.cs
  - src/SqlFlow.Catalog/Migrations/20261024215740_MaintenanceStorageAreas.cs
  - gui/src/features/maintenance/MaintenancePage.tsx
  - gui/src/features/maintenance/StorageAreaSheet.tsx
  - gui/src/features/maintenance/CleanupSheet.tsx
  - gui/src/features/maintenance/storageText.ts
---

# Maintenance: what the catalog's database holds, what is kept, and the safe purge

**Settings > Maintenance** answers three questions about the catalog's database for the person who runs the estate:
what takes the space, what is safe to remove, and what has to stay. Every table of the database belongs to a **storage
area**: tables (or one column of a table) that hold one kind of data, named as a person thinks of it ("SQL statements",
"Runs", "Chat conversations"). SQLFlow describes its own areas, and a host module describes the areas of its own schema
through the same contract (below), so the page accounts for the whole database whatever runs on it. A table no area
describes is listed under **Not described**, so nothing goes unaccounted.

Nothing is removed by default. An area that may be cleaned up keeps everything until an admin sets how long its data
stays; the areas that already clean themselves name the rule they follow.

## The three verdicts

Each area carries one verdict, about its data rather than its size:

| Verdict | Means | What maintenance does |
| --- | --- | --- |
| **Safe to remove** | Removing it loses nothing anyone needs: a superseded copy, a duplicate, or work in progress that has finished. | It cleans itself by a rule the area names, or an admin sets a limit. |
| **Optional** | It may be removed, at a cost the area states in words: history, detail or timings nothing else holds. | Nothing, until an admin sets a limit or cleans it up. |
| **Keep** | Needed: it explains a run, a record or an action, holds state something reads, or holds statistics. | Nothing set on this page removes it; it leaves only by its own lifecycle (with its repository, its user, its flow). |

A kept area that grows with use and that nothing removes is flagged **Grows with use**, so growth nobody manages is
visible. Statistics count as something to keep: the run row holds every run's totals and durations, so cleaning a run's
statements or events never touches what Data streams and run history read.

## The page

- **The answer first.** One sentence says how much is stored, how much of it may be removed at no loss and at a cost,
  and that the rest is kept; the line under it says whether the automatic cleanup runs and how many areas have a limit.
- **Tiles** for everything stored and for each verdict, with its size and its meaning; a tile filters the grid.
- **The grid**, by verdict (safe, optional, keep) or by subject (Run history, Notifications, ...), the largest entry
  first in each group, with a search over area names, what they hold and their tables. Each row shows the area's size
  (with a bar against the largest of its group, and its share of everything on hover), its rows, how its data leaves
  (the limit and what it would remove now, the rule it cleans itself by, or how it leaves at all), and its last
  cleanup. The grid fits the window and scrolls under a sticky header.
- **An area's sheet**, opened from its row: what it holds; its verdict and the reason (why it is kept, why removing it
  is safe, or what removing it costs); what a cleanup always keeps; how its data leaves the database; for an area that
  takes a limit, its **Retention** (the limit in force, who set it and when, and **Remove limit**); its cleanups; and its
  tables with their rows and sizes. Its buttons open **Clean up** on that area alone, and the page where the data is
  managed (Runs, Insights, the Nodes page).
- **Clean up**, the safe purge, opened from the page's header (and from each row and sheet of an area a cleanup can take,
  on that area alone); see below.

Sizes come from SQL Server's own metadata (`sys.partitions` and `sys.allocation_units`), so measuring a table of a
billion rows costs what measuring an empty one does. A table's size includes its indexes and the internal tables built
on it (a full-text index's fragments). An area that clears one column (a free-text log, say) is measured by the bytes of
that column's values, and the area holding its table is measured without them, so the areas add up to the database.
**Measure again** reads them anew; the page measures again every minute on its own.

A cleanup frees space inside the database, which new rows then reuse. It does not shrink the database's files on disk;
that stays a database administrator's decision.

## Clean up: the safe purge

**Clean up**, in the page's header, opens a panel that lists every area a cleanup can take, in two parts: what is safe
to remove, ticked from the start, and what is optional, not ticked, each saying what removing it costs. It names the
areas that clean themselves too, so nobody looks for a way to purge them. One age applies to all (30 days, 90 days,
1 year, 2 years, or any number of days), and as soon as it is chosen every area shows how many of its unit (statements,
events, attempts, logs, runs) are older, and about how much space that frees, or that nothing is that old; nothing has
to be pressed to see it, and nothing changes. Opened from an area's row or sheet, only that area is ticked, at its limit
when it has one.

**Clean up** at the panel's foot, after a confirmation that lists each area and its count, says what every area always
keeps and warns when an optional area is ticked, starts a cleanup of each ticked area; each row then follows its cleanup
to its end ("40 removed"), and a toast says how the whole cleanup ended. Ticking **Keep doing this** first sets the age
as the limit of the ticked areas, so the automatic cleanup removes what grows older from then on.

Anyone signed in may open the panel and see the counts; cleaning up and setting a limit are an admin's, since what a
cleanup removes cannot be brought back. Without the admin role the panel says so, and its button stays disabled.

A limit, once set, shows on the area's sheet with who set it and when; **Remove limit** there takes it off. Its days lie
between the least and the most the area takes (0 to 36,500 for the run trace), and what the area always keeps (each
flow's latest run, each record's latest attempt) is kept whatever the days. Every cleanup, asked for or automatic, is
logged with who asked (the signed-in account, by its subject) or who set the limit an automatic one applied.

A cleanup asked for runs in the background on the control plane that received it, so a large backlog never meets a
request timeout, and its entry in the area's history stays open while it runs; the panel follows it and says how it
ended. Only one cleanup of an area runs at a time on a control plane (a second is refused with `409`). A cleanup stopped
with its control plane is closed as interrupted when that host next cleans up or runs the automatic pass, or by any
host once it has been open for 12 hours; what it removed stays removed, and cleaning up again finishes it. Every deletion
goes in bounded statements, each its own short transaction, so no cleanup holds a long lock on a table the platform
writes to, and two cleanups at once remove each row once.

### The automatic cleanup

`ControlPlane:Maintenance:Enabled` (on by default) runs a pass every `ControlPlane:Maintenance:PollSeconds` (3600) on
every control-plane replica. A pass cleans every area that has a limit, records each cleanup that removed something or
failed (an hourly pass that finds nothing writes nothing), closes interrupted cleanups, and trims the cleanup log to its
last year. An area with no limit is never touched, so a pass removes nothing until an admin sets one. With the pass off,
data goes only when an admin cleans up an area.

The settings that configured the old run-trace sweep, `ControlPlane:RunTrace:*`, are refused at startup with the name of
this section.

### The run trace

Two of SQLFlow's areas take a limit, and both clean the same runs: a run whose status is succeeded, cancelled or
skipped, written before the cut-off, that is not its pipeline's most recent run. Each pipeline therefore keeps the
statements and events of its latest run and of every failed run, and every run queued or running keeps its own.

- **SQL statements** (`catalog.RunStatement`) are **safe to remove**: a flow generates nearly the same SQL on every run,
  changing only when a table's schema does, and schema changes are recorded on their own.
- **Run events** (`catalog.RunEvent`) are **optional**: removing a run's events removes its Events tab and its steps
  from Insights' step timings. The run's totals stay on its run row.

A limit set for the SQL statements before this page existed carries over to the area `sqlflow.run-statements`.

## SQLFlow's areas

| Area | Tables (`catalog.`) | Verdict | How its data leaves |
| --- | --- | --- | --- |
| Runs | `Run`, `RunGroup` | Keep, grows | Deleted with its repository |
| Trigger requests | `TriggerRequest`, `TriggerRequestSchedule`, `TriggerRequestRun` | Keep, grows | Never removed ([External triggers](external-triggers.md)) |
| Run files and results | `RunFile`, `RunAssertion`, `RunSurrogateKey`, `RunHealthCheckMetric` | Keep, grows | Deleted with its repository |
| SQL statements | `RunStatement` | Safe to remove | The limit an admin sets (above) |
| Run events | `RunEvent` | Optional | The limit an admin sets (above) |
| Flow versions | `FlowVersion` | Keep | One row per distinct flow document, never removed |
| Repositories and flows | `Repo`, `RepoSource`, `Pipeline`, `PipelineColumn`, `FlowDependency` | Keep | Rewritten by each sync; deleted with the repository |
| Objects and lineage | `Object`, `ObjectColumn`, `ObjectRelationship`, `LineageEdge` | Keep | Rewritten by each sync, which forgets objects nothing knows |
| Schema changes | `SchemaChange` | Keep, grows | Deleted with its repository |
| Schedules | `Schedule`, `ScheduleMember`, `ScheduleParent` | Keep | Rewritten by each sync |
| Subscribers | `Subscriber` and its eight report and model tables | Keep | Rewritten by each sync |
| Sync and discovery traces | `ActivityEvent` | Safe to remove | The last five attempts per subject; a discovery's lines after 2 days; a trigger request's one trace stays with the request |
| Nodes | `Node` | Safe to remove | After `Dispatch:NodeRetentionHours` offline (24) |
| Dispatch and worker pools | `DispatchLease`, `WorkerPool` | Keep | Updated in place |
| Compute tasks | `ComputeTask` | Keep, grows | Never removed |
| Prepared queries | `QueryPlan` | Safe to remove | 24 hours after it was prepared |
| Notification events | `NotificationEvent` | Safe to remove | `Notifications:EventRetentionDays` (30) |
| Sent notifications | `NotificationDelivery` | Safe to remove | `Notifications:DeliveryRetentionDays` (90) |
| Digests | `NotificationDigest` | Safe to remove | `Notifications:DigestRetentionDays` (365) |
| Subscriptions | `NotificationSubscription`, `NotificationWatermark` | Keep | Each person manages their own |
| Chat conversations | `ChatConversation`, `ChatMessage` | Keep, grows | Each person deletes their own |
| AI contexts and saved answers | `AiContext`, `ColumnPolicy`, `SemanticObject`, `SemanticRelationship`, `SemanticMeasure`, `SemanticLayerSettings`, `SemanticReportSpec`, `SemanticExample` | Keep | Removed by an admin, or with the repository |
| Users and access tokens | `User`, `Role`, `AccessToken` | Keep | Managed on the Users page |
| External triggers | `ExternalTrigger` | Keep | Managed on the External triggers page; a configured one is written at every start |
| Retention settings | `RetentionSetting` | Keep | One row per area |
| Cleanup log | `MaintenanceCleanup` | Safe to remove | A year after the cleanup |
| Catalog schema history | `__CatalogMigrationsHistory` | Keep | Never removed |

The notification areas are cleaned by the notification service, and only while `Notifications:Enabled` is on (with it
off nothing writes them either). Each area's sheet quotes the configured values, not these defaults.

## The API

| Route | Policy | Does |
| --- | --- | --- |
| `GET /api/v1/maintenance/storage` | `read` | The whole report: the database's name and data-file bytes, whether the automatic cleanup runs and how often, every area (owner, group, title, verdict `keep`/`safe`/`optional`, what it holds, the reason, its lifecycle and brief, whether it grows, where it is managed, its tables with rows and bytes, its retention with the limit set, by whom and when, and how many a cleanup at that limit would remove now, its last cleanup, whether one is running), and the tables no area describes |
| `GET /api/v1/maintenance/areas/{areaId}/cleanups?take=` | `read` | An area's cleanups, newest first (50 by default, at most 200) |
| `POST /api/v1/maintenance/areas/{areaId}/preview` | `operate` | Body `{ "days": n }`: the cut-off and how many of the area's unit a cleanup would remove; changes nothing |
| `PUT /api/v1/maintenance/areas/{areaId}/retention` | `admin` | Body `{ "days": n }` sets the limit, `{ "days": null }` removes it; records who and when |
| `POST /api/v1/maintenance/areas/{areaId}/cleanup` | `admin` | Body `{ "days": n }`: starts a cleanup and answers `202` with its open entry; `409` while one of the area runs, `400` for days outside the area's range or an area that takes no limit, `404` for an area nobody describes |

The read surface these routes sit on is closed to an assistant run's delegated token.

## For a module: describing its own storage

A host module registers an `IStorageContributor` with `ControlPlaneModuleServices.AddStorageContributor<T>()`. The
contributor names its owner and lists its `StorageArea`s; each area has a stable id (dot-separated lowercase parts led by
the module's own, at most 128 characters, never changed once shipped, since its limit and history are stored under it),
a group, a title, a verdict, what it holds, the reason for its verdict, its lifecycle and a brief of at most 40
characters for the grid, whether it grows, the GUI route where it is managed, and either its tables (by name, or every
table of a schema whose name starts with a prefix, for tables created at run time) or one column of a table another area
holds. An area that may be cleaned up carries a `StorageRetention`: what it always keeps, the unit its count is in
("attempts", "runs"), and its least and most days; the contributor's `CountAsync` and `CleanAsync` then count and remove
what is older than a cut-off. The contributor is resolved per request and per pass, so it may depend on scoped services,
and it should resolve its database only when it counts or cleans, so describing the areas never opens a connection.

The control plane checks every contributor's areas together as it starts and refuses to start, naming the contributor
and the area, when an id is malformed or used twice, a text is missing, an area to keep takes a limit, an optional area
takes none, an area holds both tables and a column (or neither), two areas hold one table, a cleared column's table is
held by no other area, or an area without a limit has no brief. A module's test should hold its areas to its own model:
every table of the model belongs to exactly one area.

## In the database

`catalog.RetentionSetting` holds one row per area an admin set a limit for (the days, or null once removed, with who
and when). `catalog.MaintenanceCleanup` holds the cleanup log: the area, `manual` or `automatic`, who asked (or who set
the limit an automatic one applied), the host that ran it, the days and cut-off, when it started and ended, how many it
removed and, when it did not finish, why. The migration `MaintenanceStorageAreas` created both and moved the old single
setting of `catalog.MaintenanceSetting` into the first before dropping it.
