---
id: concept-control-plane
title: "The control plane: API surface, configuration, bootstrap, managed sync"
type: concept
summary: The ASP.NET Core control plane host, its /api/v1 surface and scopes, ControlPlane configuration, first-run bootstrap, and managed git-to-catalog sync.
keywords:
  - control plane
  - api/v1
  - scopes
  - configuration
  - bootstrap
  - repo sources
  - run trigger
  - lineage api
related:
  - cli-worker
  - concept-run-inputs
  - concept-authentication-and-identity
  - concept-shadow-catalog
  - guide-deployment
  - guide-github-app
  - concept-maintenance
  - concept-external-triggers
sourceRefs:
  - src/SqlFlow.ControlPlane/Background/RunReferences.cs
  - src/SqlFlow.ControlPlane/Background/BootstrapSteps.cs
  - src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - src/SqlFlow.ControlPlane/Api/BootstrapEndpoints.cs
  - src/SqlFlow.ControlPlane/Program.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Infrastructure/CatalogConnectionProvider.cs
  - src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncService.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncWaiter.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RepoSourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunSetTiming.cs
  - src/SqlFlow.ControlPlane/Api/PipelineRunDurations.cs
  - src/SqlFlow.ControlPlane/Api/SearchEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/NodeEndpoints.cs
  - src/SqlFlow.Catalog/RepoSourceStore.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/DispatchLeaseStore.cs
  - src/SqlFlow.Catalog/RunContextStore.cs
  - src/SqlFlow.Catalog/RunTraceStore.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Node/GitMaterializer.cs
  - src/SqlFlow.Dispatch/Dispatcher.cs
  - src/SqlFlow.Dispatch/DispatchState.cs
  - src/SqlFlow.Dispatch/DispatchOptions.cs
  - src/SqlFlow.ControlPlane/Dispatch/DispatchService.cs
  - src/SqlFlow.ControlPlane/Dispatch/NodeProtocolEndpoints.cs
  - src/SqlFlow.ControlPlane/Dispatch/DispatchEndpoints.cs
  - src/SqlFlow.SourceControl/GitHub/GitHubAppEnvironment.cs
---

# The control plane: API surface, configuration, bootstrap, managed sync

The control plane (src/SqlFlow.ControlPlane) is an ASP.NET Core host that serves the HTTP API over the shadow catalog, triggers and queues runs, fires schedules, and keeps the catalog synced from git. It composes the identical engine wiring as the CLI and standalone workers via `AddSqlFlowEngine()` (src/SqlFlow.ControlPlane/Program.cs), so a run triggered over the API behaves exactly like a run started by `sqlflow run`: same source readers, same secret resolution chain, same connection registry, same `DocumentExecutor`.

It exists so that a team has one place to observe the estate (repos, pipelines, runs, lineage, nodes), one privileged surface to operate it (trigger, cancel, schedule, sync), and zero manual steps to keep the queryable shadow catalog current with git.

## Host composition

`src/SqlFlow.ControlPlane/Program.cs` builds the host in one pass:

- Configuration is bound from the `ControlPlane` section and validated eagerly at startup (`options.Validate()` plus `ValidateOnStart()`), so a misconfigured deployment never starts serving.
- The catalog is read through a pooled `CatalogDbContext` with `NoTracking` query behavior and `EnableRetryOnFailure`. A unit of catalog work that must be serializable runs through `CatalogTransaction`, which hands its connection back to the pool at READ COMMITTED (`PooledIsolation`, src/SqlFlow.Catalog/PooledIsolation.cs): SQL Server keeps a session's isolation level when the pool lends the connection again, so the next read on it would otherwise be serializable too, take range locks, and be chosen as a deadlock victim by the writers it blocks. A host module running its own unit at SERIALIZABLE or REPEATABLE READ uses the same helper.
- Background services host bootstrap provisioning (`BootstrapProvisioningService`), the dispatcher (`DispatchService`), the in-process node (`RunExecutionWorker`, only when `Worker:Enabled` is true), the schedule scanner (`SchedulerService`), and the managed git sync (`RepoSyncService`).
- The run queue lives in this process: the trigger endpoint journals a `queued` row through `IRunDispatcher` and tells the in-memory dispatcher, which hands work to nodes that poll for it and journals every placement with plain conditional updates (see "The dispatcher" below). Runs survive a restart (the dispatcher rebuilds from the journal) and are visible to the read API from the moment they are queued.
- Every response passes through `CorrelationIdMiddleware`, and errors are surfaced as RFC problem details (`AddProblemDetails` plus a `GlobalExceptionHandler`).
  A request whose client went away is not an error: whatever it throws from then on (a database provider can report its cancelled command as an error of its own, as SqlClient's "Operation cancelled by user" does) ends as a cancellation (`ClientAbortMiddleware`), logged at debug and answered 499.
- OpenAPI is mapped at `/openapi/v1.json` via `MapOpenApi()`.

### Authentication and scopes

JWT bearer validation pins the algorithm to HS256 only (`ValidAlgorithms = [HmacSha256]`), validates issuer, audience, lifetime, and signing key, uses a 30 second `ClockSkew`, and sets `MapInboundClaims = false` so the `sub` and `scope` claim types stay as issued.

Four authorization policies gate the surface, driven by a space-delimited `scope` claim:

| Policy | Requirement |
| --- | --- |
| `read` | Any authenticated user |
| `operate` | Any authenticated user (see below) |
| `admin` | `scope` claim contains `admin` |
| `node` | `scope` claim contains `node`; the node protocol under `/api/v1/node` only |

The privilege model has two tiers for people: any authenticated user gets the operational product, and only user administration is fenced behind `admin`. The `node` scope is fleet traffic, not a person's session: it is carried by the personal access token a compute node presents, and the built-in `admin` role holds it so an admin can mint one.

The sign-in surface under `/api/v1/auth` maps `GET /auth/providers` and `POST /auth/login` always, `POST /auth/exchange` only when `AzureAd:Enabled` is true, and the break-glass `POST /auth/token` only when `Jwt:BootstrapSecret` is set. See ./authentication-and-identity.md for token issuance, local users, and Entra SSO.

### API surface

All endpoints live under `/api/v1`.

Read surface (policy `read`):

| Endpoint | Purpose |
| --- | --- |
| `GET /repos`, `GET /repos/{id}` | Repository registry |
| `GET /pipelines`, `GET /pipelines/{id}` | Pipeline registry (filters: `repoId`, `kind`, `active`, `name`) |
| `GET /pipelines/{id}/definition` | The pipeline's parsed definition as JSON |
| `GET /pipelines/{id}/parameters` | The run controls that apply to the flow and, for a registered kind's flow, what its document declares a run may be given: its parameters, their defaults, choices and lookups, and its run settings ([Run inputs](run-inputs.md)) |
| `GET /pipelines/{id}/columns` | Declared and detected pre-ingestion transform columns |
| `GET /pipelines/{id}/files`, `GET /pipelines/{id}/files/stats` | Every distinct file the pipeline has processed (deduplicated by name and path), and the size profile over them: average, median, standard deviation, extremes, totals, and a window over the newest files, so a caller can tell a normal delivery from an anomalous one |
| `GET /runs`, `GET /runs/{runId}` | Run history and detail (filters include `repoId`, `pipelineId`, `flowKind`, `status`, `success`, `flowName`, `batch`, `latest`, `runId`). A summary carries `startUtc` and `endUtc` beside `durationSeconds`, so how long a running run has been executing is known before it has a duration |
| `GET /runs/groups/{groupId}` | One run group: its mode, anchor and member counts by state, and when it ran and how long it took: `startedUtc` (its first member's start), `endedUtc` (its last member's end, null while a member is queued or running) and `durationSeconds` between them, the wall-clock time of the set rather than its members' durations added up. Time spent waiting before the first member started is `startedUtc - enqueuedUtc` |
| `GET /runs/{runId}/files` `/assertions` `/statements` `/surrogate-keys` `/health-metrics` | Per-run drill-down |
| `GET /runs/{runId}/trace` | The run's consolidated trace: canonical engine events (file progress, resolved watermarks, decisions, stage summaries, warnings) interleaved with the generated SQL statements by timestamp |
| `GET /runs/{runId}/trace/text` | The whole trace rendered as one plain-text document (run header, then every entry with SQL inline): the Copy-trace surface, and the form to hand an LLM when debugging a run |
| `GET /runs/{runId}/trace/stream` | The trace as Server-Sent Events while the run executes: one `entry` frame per new trace entry, a final `end` frame at the terminal status (the client then refetches the paged endpoint, which holds the authoritative re-projection); `afterEventId`/`afterStatementId` cursors resume a dropped connection |
| `GET /pipelines/{pipelineId}/runs/durations` | How long the pipeline's runs take: `runs`, `medianSeconds`, `averageSeconds`, `fastestSeconds` and `slowestSeconds` over its newest 50 succeeded runs (a failed run's duration says when it failed, not how long the work takes), `last` (the newest run that started and ended, whatever its outcome) and `running` (the run executing now), each `{runId, status, startUtc, endUtc, durationSeconds}`. A run whose work was spread over members of its own pipeline is read through its root. 404 for an unknown pipeline |
| `GET /pipelines/{pipelineId}/trace`, `/trace/text` | The pipeline's LATEST run's trace (paged JSON or plain text) without resolving a run id first: the entry point for "show me what its last run did" when debugging a pipeline, by hand or from an LLM tool |
| `GET /runs/groups/{groupId}/stream` | The run group as Server-Sent Events while it executes: a `member` frame (a run summary with its newest trace event as the "last action") whenever a member changes, a full snapshot on connect, one `end` frame with the final rollup once every member is terminal |
| `GET /lineage/objects`, `/lineage/objects/detail`, `/lineage/objects/columns` | Lineage objects |
| `GET /lineage/objects/refresh` | How an object is populated and how often it updates: the flows that write it, each with its latest run and the schedules that fire it (cadence, next fire); a view with no writing flow reports the modules its content derives from |
| `GET /lineage/objects/join-paths` | ALL the ways to join one object to others: a breadth-first walk of the interpreted relationship graph returning each route as an ordered chain of hops with a ready-to-paste ON clause. `target` narrows to routes reaching one named table (through a bridge when they are not related directly), `maxHops` 1-4 (default 2). Rival routes between the same pair are kept, not deduplicated, and each is ranked by its weakest link |
| `GET /lineage/objects/graph` | The transitive lineage of one object: BFS upstream (where the data comes from) and downstream (what depends on it), each step depth-annotated and naming the flow or module carrying the hop; `direction=upstream\|downstream\|both`, `depth` 1-8 (default 3), capped with a `truncated` flag |
| `GET /repos/{repoId}/lineage/edges`, `/repos/{repoId}/waves`, `/repos/{repoId}/dependencies` | Repo-scoped lineage |
| `GET /search/all` | One global search fanned across every surface below, each category counted in full with a preview of top hits; the reply echoes the parsed `tokens` (a multi-word term matches word by word, every word required, so "ferry passengers" finds `FerryPassengers_PerDeparture`) |
| `GET /search/objects`, `/search/columns`, `/search/definitions` | Cross-repo data dictionary and code search (objects by name, synced columns by name, module bodies and emitted DDL) |
| `GET /search/files`, `/search/flows` | Processed files by name/path; flow YAML by name, path, or body text |
| `GET /search/flow-columns` | The columns FLOWS produce, matched by output name, source column, or the SQL expression computing them: the reverse "which pipeline computes column X" lookup, independent of any warehouse schema sync |
| `GET /search/statements` | The SQL runs actually executed, grouped to one row per (flow, step) with an occurrence count and the newest sample; searches a 90-day window by default (`days=0` for all retained history) |
| `GET /search/subscribers` | The consumers of the warehouse (dashboards, reports, workbooks, applications) by name, tool, owner, description, notes, location (the report URL or workbook path), or declaring file. Searching `Incomplete dataset` lists every consumer whose registered lineage is only partial |
| `GET /schedules`, `GET /schedules/{id}` | Schedules |
| `GET /schedules/{id}/parameters` | What each flow the schedule runs is given when it fires: the value of each parameter and where it comes from, and what would refuse its run ([Run inputs](run-inputs.md#what-a-schedule-gives-each-flow)) |
| `GET /nodes` | Worker fleet with a derived online flag |
| `GET /repos/sources` | Managed repo sources |
| `GET /summary` | Estate summary |
| `GET /dataops/capabilities` | Whether the data-operations surface is enabled here, the operations available, and the linked servers a baseline comparison may name (concept-data-operations) |
| `GET /maintenance/storage`, `GET /maintenance/areas/{areaId}/cleanups` | What the catalog's database holds, area by area, with each area's verdict, size, limit and cleanups, and one area's cleanup log ([Maintenance](maintenance.md)); closed to an assistant run's delegated token |
| `GET /trigger-requests`, `GET /trigger-requests/{id}`, `/events`, `/summary`, `/runs/{runId}/trace` | Trigger requests: the list, one request with its runs read live, its trace after a cursor, its Markdown summary, and a run's trace as text; an external trigger's credential reaches only its own ([External triggers](external-triggers.md#following-a-request)) |

Operate surface (policy `operate`):

| Endpoint | Purpose |
| --- | --- |
| `POST /runs` | Trigger a run or a run group (202 Accepted) |
| `POST /pipelines/{id}/parameters/{name}/values`, `POST /pipelines/{id}/parameters/explain` | A parameter's values to pick from (its choices, or its kind's lookup), and what a run with a form's inputs would take and what would refuse it; nothing is queued ([Run inputs](run-inputs.md#the-routes)) |
| `POST /runs/{runId}/cancel` | Cancel a run (200 `cancelled` from queued; 202 `cancelling` while running) |
| `POST /runs/groups/{groupId}/cancel` | Cancel a whole run group (queued members cancelled, running members requested) |
| `POST /schedules`, `POST /schedules/{id}/pause`, `POST /schedules/{id}/resume`, `DELETE /schedules/{id}` | Ad-hoc schedule management |
| `POST /repos/sources` | Register or update a managed repo source (upsert) |
| `POST /repos/sources/{id}/sync` | Force a sync now |
| `POST /repos/discover` | Preview a repo's flows without importing (the selective-scan wizard) |
| `POST /repos/sources/{id}/proposals` | Propose pipelines to a source as a pull request (`author` scope) |
| `POST /datasources/tasks` | Enqueue an ad-hoc compute task against a datasource: browse/introspect, detect a unique key, the warehouse-health DMV probes, and (behind `DataOps:Enabled`) `duplicateKeys` and `compareBaseline`. `runQuery` is refused here: a query runs only through a prepared plan. References only; the executing node resolves the credential |
| `POST /datasources/tasks/{taskId}/cancel` | Cancel a queued or running compute task |
| `POST /dataops/queries/prepare` | Validate an ad-hoc SELECT and mint a one-time plan token; nothing runs. Behind `AiContext:Enabled` |
| `POST /dataops/queries/{planId}/run` | Redeem an approved plan token and queue the query. Takes a token, never a statement, so the confirmation cannot be skipped |
| `POST /maintenance/areas/{areaId}/preview` | What a cleanup of a storage area at the days named would remove; changes nothing ([Maintenance](maintenance.md)) |
| `POST /trigger-requests`, `POST /trigger-requests/{id}/cancel` | Sync a repository and fire a selection of its schedules (202, or 200 with the request already made for a repeated `requestKey`), and cancel a request; open to an external trigger's credential for its own repository and schedules ([External triggers](external-triggers.md#trigger-requests)) |

Admin surface (policy `admin`): `GET/POST /users`, `POST /users/{id}/role` `/activate` `/deactivate` `/password`, and `GET /roles` (src/SqlFlow.ControlPlane/Api/UserEndpoints.cs); a storage area's limit and cleanup, `PUT /maintenance/areas/{areaId}/retention` and `POST /maintenance/areas/{areaId}/cleanup` ([Maintenance](maintenance.md)); and the external triggers, `GET/POST /external-triggers`, `GET /external-triggers/setup`, `GET/PUT/DELETE /external-triggers/{id}` and `POST /external-triggers/{id}/enable` `/disable` `/key` ([External triggers](external-triggers.md)).

Anonymous: an external trigger exchanges its proof for its credential at `POST /auth/trigger/github` (a GitHub Actions job's OpenID Connect token) and `POST /auth/trigger/key` (its key) ([External triggers](external-triggers.md#how-a-trigger-proves-who-it-is)).

### Health probes and rate limiting

- `GET /health/live` has no dependencies (its predicate excludes every check); `GET /health/ready` runs the catalog database check, the module database verification and any check a module adds; `GET /health/startup` runs the start-up pass's check (see [the start-up pass](#the-start-up-pass)). All three are mapped with `DisableRateLimiting()`, so probe traffic behind a shared egress IP can never be throttled into a false negative.
- A global fixed-window rate limiter partitions per authenticated subject (the `sub` claim) or, when anonymous, per client IP: default 120 permits per 60 second window, `QueueLimit = 0`, and HTTP 429 on rejection. The limiter runs after authentication so the partition can key on the subject.

### CORS and reverse-proxy awareness

- CORS is an explicit allowlist (`ControlPlane:Cors:AllowedOrigins`). An empty list means no CORS policy is registered at all: same-origin only.
- Forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) are honored only when `ControlPlane:Proxy:Enabled` is true, and only from the proxies listed in `KnownNetworks` (CIDRs) or `KnownProxies` (IPs), with `ForwardLimit` hops (default 1). The forwarded-headers middleware runs first in the pipeline so the rate limiter and login throttle see the real client address. Enabling the proxy section while trusting no proxies is a startup error ("ControlPlane:Proxy is enabled but trusts no proxies; ...").

### Fleet visibility

`GET /nodes` (src/SqlFlow.ControlPlane/Api/NodeEndpoints.cs) lists workers the dispatcher has heard from, most recently seen first. A node is reported `online` when its last poll is within the last 60 seconds, computed at read time. The dispatcher keeps the fleet in memory and flushes each node's last poll (build, pool, `BusyRuns`, `RunSlots`) to the catalog's `Node` table every `Dispatch:NodeFlushSeconds`, so a fleet of hundreds costs the catalog a few dozen updates a minute rather than one per heartbeat; `BusyRuns` and `RunSlots` feed the replica target the autoscaler reads back from the control plane (`GET /node/scale-target`, below), so occupied workers hold their replicas while idle ones remain the reclaimable surplus and the fleet is sized by what its nodes actually execute at once. `GET /nodes/pools` shows each pool's target beside the terms it was built from, the same resolution the scaler is answered with, and, for each pool this control plane's own node serves (with `Worker:Enabled`, the default pool and the pools `Worker:Pools` names), that node as `builtInNode` (its name and run slots). With managed workers off and a built-in node, the Nodes page says that the control plane starts no worker and the target only takes effect where an autoscaler reads it, rather than showing workers as starting. `GET /dispatch` returns the dispatcher's own view: every queued run with the gate holding it back (`pipeline-busy`, `wave-gated`, `group-cap`, `no-eligible-node`), every lease with its node and expiry, the fleet, ownership, and the last housekeeping passes.

### The dispatcher

The dispatcher (`Dispatcher` in src/SqlFlow.Dispatch/Dispatcher.cs, hosted by `DispatchService` in src/SqlFlow.ControlPlane/Dispatch/DispatchService.cs) holds every queued and executing run and compute task in memory and makes each placement decision under one lock: an untargeted run goes to any node and a pooled run only to a node serving that pool; a run group member is eligible only when every lower wave is terminal and while fewer than its group's cap are executing; and no two runs of one pipeline execute at once outside a fan-out family (a running run and the member runs it fanned out over, `IRunFanOut`), because each flow stages through one canonical work table. Eligible runs are handed out oldest first, and a run a gate holds back never blocks a later run that is eligible. A fan-out may ask for a node per member (`IRunFanOut.EnqueueAsync(members, wholeNode: true, ct)`, journaled as the run's `WholeNode`, migration `RunWholeNode`): such a member is handed only to a node that reports every run slot free, alone in its batch, and is skipped on any other node without holding back the runs behind it; its hand-out says so (`RunSpec.WholeNode`), and the node gives it every slot, so it is handed nothing beside it until the member ends.

Memory is authoritative for placement; the catalog is the journal. An enqueue writes the `Run` row before the dispatcher learns of it; a hand-out is reserved in memory, journaled (`queued` to `running`, the attempt advanced, conditional on the row still being queued at that attempt), and only then handed to the node; an outcome is journaled under the fence and then dropped from memory. A completion's consequences (a failed group member's queued dependents skipped, a fan-out root's unfinished members ended) are written after its commit, as guarded statements outside any serializable transaction, and before the dispatcher hears the answer, so no dependent is handed out between the two. Nothing in that journal is more than a plain conditional `UPDATE`, so no locking hint or provider-specific feature is involved. At activation the dispatcher rebuilds from the journal: queued rows are queued, running rows are leased to their recorded node under a grace lease so a node still executing them reattaches on its next poll. A reconcile pass every `Dispatch:ReconcileSeconds` diffs memory against the journal and heals anything that bypassed the in-process notify (a run cancelled straight against the catalog by `sqlflow runs cancel --db`, an enqueue on a passive replica), acting on additions at once and on removals only when they persist across two passes.

Every run, every member of a run group and every compute task is queued carrying the values the control plane supplies for its `${env:NAME}` references, from the `IRunReferenceSource` a host module registers (src/SqlFlow.ControlPlane/Background/RunReferences.cs): the connections and settings a deployment keeps in one place instead of on every node. The node resolves the run's references with them ahead of its own environment, and a run's trace names where each value came from; with no source registered nothing is attached. See [Values the control plane supplies with a run](connections-and-secrets.md#values-the-control-plane-supplies-with-a-run).

Nodes pull work through the node protocol under `/api/v1/node` (src/SqlFlow.ControlPlane/Dispatch/NodeProtocolEndpoints.cs), authenticated with the `node` scope, and it is the only connection a node has: a node never opens a catalog connection. `POST /node/poll` is the heartbeat, the lease renewal, the cancel channel and the hand-out in one long-polled call (held for up to `Dispatch:LongPollSeconds` when nothing is available, answered the instant something is); each handed-out run carries its execution spec (the repo and pipeline identity, the remote and root path, the commit pin, the flow-version hash, the run parameters, the git credential reference), which the dispatcher reads from the catalog before it journals the hand-out, so a read that fails leaves the run queued with no attempt consumed. While a run executes, `GET /node/flow-versions/{hash}` serves the snapshotted YAML the enqueue staged (content-addressed, so a node fetches each version once), `POST /node/runs/{runId}/context` resolves the lineage facts the run depends on (the downstream watermark table, the landing-reset verdict; src/SqlFlow.Catalog/RunContextStore.cs) once the node has parsed the document and knows the flow participates, and `POST /node/runs/{runId}/trace` takes the run's generated SQL and canonical events in batches (every 250 ms, 200 items or 2 MB of text, whichever first; src/SqlFlow.Catalog/RunTraceStore.cs) so the Statements and Events views stream while the run executes. `POST /node/runs/{runId}/outcome` and `POST /node/tasks/{taskId}/outcome` report results. `GET /node/scale-target?pool=` is the one call on this surface made not by a node but by the fleet's autoscaler (KEDA's `metrics-api` scaler, presenting the same node token): a pool's replica target, `replicas`, computed from the journal on any replica (src/SqlFlow.Catalog/ScaleTargetStore.cs) as the greatest of the demand (the eligible backlog, those queued runs no gate holds back once the pool's runs ahead of them in hand-out order have taken their places, so a group under its cap counts no more of the pool's members than the places it has left and a pipeline queued twice for the pool counts once, while a run queued for another pool takes no place, divided by what one node of the pool executes at once as the nodes report it, rounded up, with each member that takes a node to itself counted as a node of its own, plus the nodes currently busy), the pool's always-on floor and an active manual override. `GET /nodes/pools` gives those members as `eligibleWholeNodeRuns` beside `eligibleQueuedRuns`, and managed workers start a worker for each one no idle worker takes. The gates are evaluated by loading the journal's queued and running rows into the same in-memory state the dispatcher hands work out with, so the scaler asks for no replica that no node could use. Every hand-out is a lease (`Dispatch:LeaseSeconds`, default 90) renewed by each poll that reports the run held; a lapsed lease is dispositioned exactly as a dead node's runs always were: requeued while attempts remain (`MaxExecutionAttempts`, 3; the interrupted attempt's live trace rows are discarded with it, so the run's trace is the successor's), recorded `cancelled` when an operator cancel was already pending, failed once the budget is exhausted (its group dependents skipped). Every hand-out increments the run's `Attempt`, which is the fencing token every per-run call presents: a node whose lease lapsed and whose run was handed out again presents a stale attempt on its context request, its trace batches and its outcome report, each of which is refused (checked in the dispatcher's memory first and in the journal again), so the successor execution's rows and result are authoritative. The node protocol has its own rate-limit partition (`RateLimit:NodePermitPerWindow`), keyed by the token's subject plus the node name every call carries in the `X-SqlFlow-Node` header, so a fleet of hundreds sharing one node token still gets a window per node and never trips the per-user limit meant for people.

Exactly one replica dispatches at a time. `DispatchService` acquires the `DispatchLease` row (src/SqlFlow.Catalog/DispatchLeaseStore.cs) with one conditional update, renews it every `Dispatch:OwnershipRenewSeconds`, releases it on a graceful stop so a revision swap hands over within one renew interval, and deactivates the dispatcher if the lease cannot be renewed before its TTL. A replica that does not own dispatch still serves the API and journals enqueues (the owner's reconcile picks them up); its node routes answer 503 `Dispatch is not active on this replica` with `Retry-After: 2`, and a node's client retries until it lands on the owner (polls with backoff; the flow-version, context, trace and outcome calls under a bounded retry budget, so a run's result is never lost to a refusal). That path exists for the overlap of a revision swap: the deployment templates run the control plane as one replica, because a second replica adds no dispatch capacity and refuses every node call that lands on it, which only slows hand-outs.

A **stopping** node is not a dead one, and the two must not be confused. When a worker is asked to stop (a SIGTERM from an autoscaler reclaiming the replica, a revision swap, an operator restart) it stops taking work but keeps executing the runs it already holds, and it keeps polling with no free slots for the whole drain precisely so its leases stay alive (src/SqlFlow.Node/RunWorker.cs). Were a draining node to go silent, its leases would lapse, its runs would be requeued, and a sibling node would re-execute work that was about to finish while the draining node's own outcome reports lost to the fence. Only when a node's drain window expires does it sever what remains, and those runs' leases then lapse for the dispatcher to requeue.

Because a requeue consumes an execution attempt, anything that severs runs routinely will eventually exhaust the budget of a healthy run and fail it with "a run that repeatedly dies mid-flight is treated as the cause". In an autoscaled fleet the usual culprit is not the flow but the platform: replicas are reclaimed on a cadence, and a termination grace period shorter than the worker's drain window turns every scale-in into a severed run. See [`sqlflow worker`](../cli/worker.md) for the drain contract and the grace-period requirement.

A node can also be **drained** (src/SqlFlow.Dispatch/NodeRegistry.cs, `Dispatcher.DrainNode`): the dispatcher hands it nothing more, and the first poll that shows it holding nothing (counting anything handed out on that same poll) answers with the restart flag, so the node finishes every run it was given and then exits on its own. A node that keeps polling after the flag is told again at most once per long poll, and a work wake that reached a poll just as its node was drained is handed on to a poll that can take the work. `Dispatcher.OwnershipEpoch` changes on every activation and deactivation, so whatever acts on a picture of the fleet can tell that the registry was cleared or rebuilt under it. Only the managed fleet drains nodes (managed workers, [guide-deployment](../guides/deployment.md)); with it off, no node is ever drained and every poll answers as before. The fleet registry tracks what draining needs: when each node last stopped holding anything, when it was drained, and when it was first told to exit, all visible in `GET /dispatch`.

## Configuration

All settings bind from the `ControlPlane` configuration section (environment variables use the `ControlPlane__Section__Key` form) and are validated at startup with messages that name the offending field (src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs).

| Key | Default | Notes |
| --- | --- | --- |
| `Catalog:ConnectionReference` | `${env:SQLFLOW_CATALOG_DB}` | A connection string or a `${env:...}` / `${keyvault:...}` reference, resolved once through the SqlFlow secret resolver and cached for the app lifetime; a transient resolution failure is retried, not cached (src/SqlFlow.ControlPlane/Infrastructure/CatalogConnectionProvider.cs) |
| `Jwt:Issuer` | `sqlflow-control-plane` | Required non-blank |
| `Jwt:Audience` | `sqlflow` | Required non-blank |
| `Jwt:SigningKey` | none | Required; at least 32 UTF-8 bytes for HS256; source it from a secret, never a literal |
| `Jwt:AccessTokenMinutes` | `720` | Range 1..1440. One token's life; a signed-in GUI rolls its token at `POST /auth/renew` rather than letting it lapse |
| `Jwt:SessionMaxDays` | `30` | Range 1..365. The absolute ceiling on a rolling session, measured from the actual sign-in |
| `Jwt:BootstrapSecret` | unset | Optional; at least 32 bytes when set; `POST /auth/token` is only mapped when set |
| `AzureAd:Enabled` | `false` | When true, `TenantId` and `ClientId` are required |
| `AzureAd:Authority` | derived | Defaults to `https://login.microsoftonline.com/{TenantId}/v2.0` |
| `AzureAd:DefaultRole` | `viewer` | Role a first-time Entra user is provisioned with |
| `Bootstrap:ApplyMigrations` | `true` | `false` logs pending migrations as a warning instead of applying them |
| `Bootstrap:AllowCreate` | `false` | When `false` (the default), startup only migrates an EXISTING catalog: a missing database or a populated non-catalog database is refused loudly and startup stops without retrying, so a wrong or mistyped connection never provisions against the wrong (possibly production) server. `true` lets startup CREATE the catalog database and initialise its schema into an empty one, for first-time provisioning or ephemeral/test databases |
| `Bootstrap:AdminUsername` / `Bootstrap:AdminPasswordReference` | unset | Must be set together; the password is a secret reference, never a literal |
| `Bootstrap:Repositories:<name>` | none | The repo sources the deployment syncs, written at every start and locked against the API (below): `RemoteUrl` or `LocalPath` (exactly one), `Branch` (`main`), `SyncIntervalSeconds` (`300`), `CredentialReference` (a `${...}` reference to a private remote's token, or a `${githubapp:...}` reference naming a GitHub App; never the token), `CredentialUsername`, `ExcludedFlowPaths` |
| `Bootstrap:DemoRepo` | unset | One repo source written the same way under its own key: `Name` and `RemoteUrl` required when configured; `Branch` default `main`; `SyncIntervalSeconds` default `300` |
| `Bootstrap:NodeTokenReference` | unset | A `${...}` reference to the node credential the deployment's workers present (`SQLFLOW_TOKEN`): `sqlf_` and at least 32 random characters, kept in the deployment's vault. Needs `AdminUsername`, the token's owner |
| `Cors:AllowedOrigins` | `[]` | Empty means same-origin only |
| `RateLimit:PermitPerWindow` / `RateLimit:WindowSeconds` | `120` / `60` | Both must be positive |
| `RateLimit:NodePermitPerWindow` | `6000` | The per-node window for the node protocol, keyed by the token subject plus the node name each call carries; must be positive |
| `Scheduler:PollSeconds` | `15` | Schedule scan cadence; must be positive |
| `ManagedSync:PollSeconds` | `30` | Repo-source scan cadence for the periodic syncs; a sync-now or a newly registered source wakes the scan at once instead of waiting for it |
| `ManagedSync:SyncNowWaitSeconds` | `90` | How long `POST /repos/sources/{id}/sync` waits for the sync it requested before it answers; 0 to 900, 0 answers at once |
| `ManagedSync:SyncAbandonedAfterMinutes` | `30` | Age past which an unanswered sync-now or a running attempt no longer counts as in progress (`syncPending`); 1 to 1440 |
| `Worker:Enabled` | `true` | `false` makes the replica API-only (no in-process node) |
| `Worker:Pools` | `[]` | Empty takes only untargeted runs |
| `Worker:MaxConcurrentRuns` / `Worker:MaxConcurrentComputeTasks` | `4` / `2` | The in-process node's slots; both at least 1 |
| `Dispatch:LeaseSeconds` | `90` | How long a hand-out lasts without a renewing poll; at least twice `LongPollSeconds` |
| `Dispatch:LongPollSeconds` | `30` | The longest a node's poll is held open; 1 to 60 |
| `Dispatch:ReconcileSeconds` | `5` | How often memory is diffed against the journal |
| `Dispatch:NodeFlushSeconds` | `5` | How often the fleet registry is flushed to the catalog |
| `Dispatch:NodeRetentionHours` | `24` | How long a silent node stays in the registry; `0` never prunes |
| `Maintenance:Enabled` / `Maintenance:PollSeconds` | `true` / `3600` | The automatic cleanup: a pass on every replica that applies the limit an admin set for each storage area, and removes nothing while none is set ([Maintenance](maintenance.md)). `RunTrace:*`, which configured the run trace's sweep before it, is refused at startup |
| `Dispatch:TaskQueuedExpiryMinutes` / `Dispatch:TaskRunningExpiryHours` | `15` / `6` | When an unclaimed or overlong compute task is failed with a hint |
| `Dispatch:MaxExecutionAttempts` | `3` | Hand-outs a run may consume before an interrupted attempt is failed |
| `Dispatch:OwnershipTtlSeconds` / `Dispatch:OwnershipRenewSeconds` | `30` / `10` | The dispatch ownership lease; the TTL is at least twice the renew interval |
| `Proxy:Enabled` | `false` | See proxy section above; `ForwardLimit` default `1` |
| `AiContext:Enabled` | `false` | AI context: a question's query (prepare/run), saved answers, and running a trusted saved answer straight away, all behind this one switch; refused with a 403 or 501 naming it while it is off. Its tuning (`ExpandSynonyms`, `SavedAnswers:*`, `AutoRun:*`, `ReportExtraction:*`) is in concept-semantic-layer |
| `DataOps:Enabled` | `false` | The engineer's data checks: the duplicate-key check and the old-versus-new baseline comparison. Both are read-only, and refused with a 403 naming this setting while it is off. Queries are AI context's, and the four warehouse-health DMV probes are a separate, older feature; neither is gated by it (concept-data-operations) |
| `DataOps:Comparison:LinkedServers` | `[]` | The linked servers a baseline comparison may name. A linked-server name becomes an identifier in generated SQL and a route into another estate, so it is configuration, never something a request chooses; an unlisted name is refused |
| `DataOps:Comparison:DefaultLinkedServer` | unset | Used when a comparison names none; must be one of `LinkedServers` or startup fails |
| `DataOps:Comparison:Databases` | `[]` | Optional narrowing to named databases on those servers. Empty permits any database the linked server's own login can reach, which is the usual case because the linked server is the boundary |

## The start-up pass

`BootstrapProvisioningService` (src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs) brings a deployment to a usable state from its configuration alone, at every start, in one ordered pass of named steps. A step that fails is retried with backoff (5s, 10s, 20s, 40s, then every 60s) from that step, so the start order of app, database and vault never matters; the steps before it are not applied again in the same pass. Every step is idempotent and never overwrites operator state.

Before provisioning anything, the `catalog` step applies the `Bootstrap:AllowCreate` guard (default `false`). Unless it is `true`, a missing database or a populated non-catalog database is a deterministic configuration mistake that retrying cannot fix, so the step is refused, a critical error is logged and the pass stops (readiness stays red) rather than conjuring a database or injecting catalog tables into the wrong server. Only `AllowCreate = true` creates the catalog database and initialises its schema into an empty one.

| Step | What it does |
| --- | --- |
| `catalog` | Applies pending EF catalog migrations when `Bootstrap:ApplyMigrations` is true (the default). With it false, pending migrations are logged as a warning: "Apply them out of band; parts of the API may fail until then." |
| `module-databases` | Migrates and verifies every module database a host registers; one missing, behind or ahead of the build refuses the step and stops the host. |
| `roles` | Seeds the built-in roles: `admin` (scopes `read operate admin author node`), `operator` (`read operate author`), `viewer` (`read`). A scope an operator added to a built-in role survives re-seeding, and a scope the definition gained later (such as `node`) is appended to an existing row (`EnsureRoleAsync`). |
| `admin` | Creates the initial admin from `Bootstrap:AdminUsername` and `Bootstrap:AdminPasswordReference` (resolved through the secret resolver) when the user is absent. An existing admin's password is never reset. A reference that resolves to nothing, or to a password shorter than the minimum, fails the step (and so readiness), and the next attempt picks up a secret put right. |
| `repositories` | Writes the repo sources `Bootstrap:Repositories` and `Bootstrap:DemoRepo` name (`RepoSourceStore.ApplyConfiguredAsync`): registers each one the catalog does not hold (due at once), brings each one it holds in line and enables it, records the setting that names it (`ConfiguredBy`), and releases each source an earlier configuration named and this one does not, keeping it as an ordinary source. It wakes the managed sync when it registered or changed one. |
| `external-triggers` | Writes the external triggers `Bootstrap:ExternalTriggers` names (`ExternalTriggerStore.ApplyConfiguredAsync`), reading each key trigger's key from the secret its `KeyReference` names, and disables and releases each trigger an earlier configuration named and this one does not ([External triggers](external-triggers.md#configured-triggers)). A secret that does not hold a key of SQLFlow's form fails the step. |
| `node-credential` | Reads the token `Bootstrap:NodeTokenReference` names, at every start, and makes it the one active node-scope token of the initial admin (`AccessTokenStore.EnsureConfiguredAsync`), revoking the one it replaced when the secret changed; with none named, revokes the one an earlier configuration set. A deployment's workers present the same secret, so they connect with no token minted by hand. |
| `worker-pool` | Seeds the default pool with an always-on floor of one worker when the pool has no desired state yet. |
| a module's steps | What a host module needs from the configuration (`IBootstrapStep`), in the order the modules registered them. |

The health check `bootstrap` (`BootstrapReadiness`), served at `/health/startup`, stays unhealthy until every step is done and names the step it waits for, or the one that failed. It is not part of `/health/ready`: a control plane with a failed step still serves the API, so whoever deployed it can ask why. `GET /api/v1/bootstrap` (read) answers each step as it last stood: its name, state (`pending`, `running`, `done`, `failed`, `refused`), what it did, the error it last failed with (secret-redacted), how many attempts it took and when it last changed. When the pass completes, the log says `Bootstrap provisioning completed.`

What the configuration sets is locked against the API, because the next start would set it back: re-registering or deleting a configured repo source (`POST /repos/sources`, `DELETE /repos/sources/{id}`) and revoking the configured node token (`DELETE /me/tokens/{id}`) answer 409 "Set by configuration", naming the setting to change instead. The repo source listing and the token listing carry `configuredBy`, and the GUI marks such a row configured and offers no edit, delete or revoke on it.

## Managed git-to-catalog sync (repo sources)

A `CatalogRepoSource` (src/SqlFlow.Catalog/CatalogEntities.cs) registers a git repository the control plane keeps synced: it periodically pulls the branch HEAD and runs the catalog sync, so nobody runs `sqlflow db sync` by hand and git stays the source of truth. Defaults: `Branch` `main`, `SyncIntervalSeconds` `300`, `Enabled` `true`. `Name` is unique and the synced pipelines and runs are attributed to it. A source's id is `FlowIdentity.FromName("reposource/{name}")`, so re-registering the same name updates in place; a new source is due immediately (`NextSyncUtc = now`).

`RepoSyncService` (src/SqlFlow.ControlPlane/Background/RepoSyncService.cs) scans every `ManagedSync:PollSeconds` (default 30, at most 50 sources per tick) for sources due to sync. A sync-now, or registering a source, makes the source due in the catalog and then wakes the loop on the host that took the request (`RepoSyncSignal`), so its sync starts at once instead of up to `PollSeconds` later; a sync already running on that host finishes first, and the woken scan follows it at once. The catalog stays the record of what is due, so a host whose managed sync is disabled, or another control-plane node, still finds the source due on its own scan. Each due sync is claimed by compare-and-swap on `NextSyncUtc` (`RepoSourceStore.TryClaimSyncAsync`), so several control-plane nodes never sync the same source twice in one interval; the claim stamps the attempt's start (`SyncStartedUtc`). The winner then:

1. Reads the branch tip from the remote without cloning (a `git ls-remote`) and materializes that commit into its own immutable folder, `<cache>/<repoHash>/<commitSha>`, the same folder a run pinned to it uses (`GitMaterializer.MaterializeBranch`, src/SqlFlow.Node/GitMaterializer.cs). The catalog sync records that folder as the repo's root. A tip that has not moved reuses the folder and writes nothing, and a new commit gets a folder of its own, so the root an earlier sync recorded is never removed or rewritten: a reader resolving a flow file under it (an operation, the repo tree) never finds it missing, and a sync that fails leaves it as it was. A commit folder whose name cannot be taken (a leftover tree something holds open; on Windows a deleted folder keeps its name until its last handle closes) is published under a unique sibling, `<commitSha>.alt-<guid>`, which is reused from then on. Each sync also sweeps, best effort, what nothing can be using: trees moved aside to free a name, staging folders older than an hour, and the previous layout's `branch` folder once unwritten for a day. A repo with no commits fails with `'<remote>' (branch '<branch>') has no commits to sync.`, and a branch the remote does not have with `'<remote>' has no branch '<branch>' to sync.`
2. Runs the exact same `CatalogSync` as the CLI's `sqlflow db sync` (the offline tier; the connected/derived tier is a separate opt-in of the CLI's `--connect`), passing the source's flow selection (see below).
3. Records the pulled commit SHA on success (`LastSyncedSha`, clearing `LastError`) or a secret-redacted error on failure (`LastError`, keeping the last successful SHA), stamping `LastSyncUtc` either way. An attempt cut short because its host is stopping records no outcome but clears `SyncStartedUtc`, so it does not show as running.

One source's failure never stops the others or the loop.

A run started without an explicit `commitSha` is pinned to `LastSyncedSha` when it is queued, so what a run executes is whatever the last finished sync pulled. That is why a sync-now answers only once its sync has happened (below): answering when the sync was merely queued let an operator commit, click "sync now", start a run, and have the run execute the previous commit.

### Git credentials (a reference, never a secret)

Every git operation on a source (the sync, a discover, the tree and history reads, a proposal, and a node checking out the commit a run is pinned to) resolves its credential through one path, `GitMaterializer.ResolveCredentialAsync` (src/SqlFlow.Node/GitMaterializer.cs), in this order:

1. **The source's own reference.** `CredentialReference` is a `${keyvault:vault/secret}` or `${env:NAME}` reference to a token, with an optional `CredentialUsername` for hosts that authenticate the username too (Bitbucket app passwords), or a `${githubapp:...}` reference naming a GitHub App. A GitHub App reference that names no repository is narrowed to the source's own repository, and one that names no permissions to what the operation needs: `contents:read` for a read, `contents:write` and `pull_requests:write` for a proposal. A reference naming another repository than the remote's is refused.
2. **The host's GitHub App**, for a source naming no reference whose remote is an `https` repository on the App's GitHub (`SQLFLOW_GITHUB_APP_ID` with `SQLFLOW_GITHUB_APP_PRIVATE_KEY` or `SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH`, and `SQLFLOW_GITHUB_APP_API_URL` for GitHub Enterprise), with a token narrowed the same way. Where the App is not installed on the repository, the resolution goes on to the next step and keeps the reason.
3. **The host's token**, `SQLFLOW_GIT_TOKEN` (optional `SQLFLOW_GIT_USERNAME`).
4. **No credential**, which serves public and local-path remotes. A remote that then asks for a credential fails with a message saying how to give one.

Only the reference is stored; the secret value is created and maintained in the vault (or, for a GitHub App, minted for each operation and reused for at most ten minutes in the process), and the API rejects a raw token that is not a `${...}` reference. The node resolves the secret itself and it never rests in the catalog or the queue. A host App that is configured only in part (an App ID without a key, a key without an App ID, or both key variables) is refused with the variable to fix rather than ignored. Each sync's trace names the credential it read with (`Reading with ...`), never the secret, and the repo source list says what each source reads with, computed from the configuration without resolving anything. [Reading a private GitHub repository with a GitHub App](../guides/github-app.md) walks through creating a read-only App.

### Preview-first selective scan

A source imports every flow document by default: every `*.yaml` file that parses as a flow, schedule and subscriber libraries aside (the same estate scan `sqlflow lineage` runs, src/SqlFlow.Lineage/Collection/FlowSetCollector.cs). To onboard a subset, `POST /repos/discover` clones the remote and lists its flows (relative path, parsed name and kind, or the error that kept the file from being read or a recognisable flow from loading, size, and redacted content for preview) WITHOUT touching the catalog. The chosen exclusions are stored on the source as `ExcludedFlowPaths` (a JSON array of repo-relative paths); the sync then projects only the included flows as pipelines. Because the sync is the single activation point, an excluded flow never becomes a catalog pipeline (so the scheduler never runs it), a previously-imported flow that becomes excluded is deactivated on the next sync (its run history is kept), and a re-included flow is imported again.

API:

- `GET /repos/sources` (read): paged list of `RepoSourceDto` with fields `id`, `name`, `remoteUrl`, `branch`, `enabled`, `syncIntervalSeconds`, `nextSyncUtc`, `lastSyncUtc`, `lastSyncedSha`, `lastError`, `credentialReference`, `credentialUsername`, `credential` (what the source reads with, as configured: `kind` `none`, `reference`, `github-app` or `host-token`, `fromHost`, a `summary` in words, and the `problem` that keeps it from being used, if any; never a secret), `excludedFlowPaths`, `createdUtc`, `updatedUtc`, `syncRequestedUtc` (the latest sync-now), `syncStartedUtc` (the start of the latest attempt) and `syncPending` (a sync-now not yet answered, or an attempt running, either younger than `ManagedSync:SyncAbandonedAfterMinutes`; `RepoSourceStore.IsSyncPending`). The GUI shows a source as syncing, and keeps its "Sync now" button busy, while `syncPending` is true.
- `POST /repos/sources` (operate): upsert with body `{name, remoteUrl, branch?, syncIntervalSeconds?, enabled?, credentialReference?, credentialUsername?, excludedFlowPaths?}`; `branch` defaults to `main`, `syncIntervalSeconds` to 300, `enabled` to true, and an omitted `excludedFlowPaths` imports every flow. A blank `name` or `remoteUrl`, or a `credentialReference` that is not a `${...}` reference, is 400. Returns 201 Created with the source id.
- `POST /repos/sources/{id}/sync` (operate): makes the source due now (recording `syncRequestedUtc` and a full lineage recompute) and wakes the sync loop so the sync starts at once, then waits up to `ManagedSync:SyncNowWaitSeconds` for the attempt that answers the request: one that started at or after it and has recorded an outcome (`RepoSourceStore.IsSyncAnswered`; an attempt already running at the request may have cloned before the commit asked for, so it does not count). 200 with the `RepoSourceDto` once answered: `lastSyncedSha` is the commit runs started from then on are pinned to, and a non-null `lastError` means the sync failed. 202 with the source still `syncPending` when the sync outlasted the wait. 404 for an unknown or disabled source ("No enabled repo source '{id}'.").
- `POST /repos/discover` (operate): body `{remoteUrl, branch?, credentialReference?, credentialUsername?}`; clones and returns the flow list for the selection wizard. Read-only; nothing is written to the catalog. A clone or credential failure is a 400 with a redacted message.
- `POST /repos/check` (operate): body `{remoteUrl, branch?, credentialReference?, credentialUsername?}`; resolves the credential a source naming that reference would read with, through the same path as a sync, and reads the branch tip with a `git ls-remote` (nothing is cloned or written). 200 with `{credential, branch, commitSha}`; a credential that cannot be resolved is a 400 `Credential unavailable`, and a remote that refuses or lacks the branch a 400 `Repository unreachable` naming the credential it was read with.
- `GET /repos/credentials` (read): what this control plane supplies to a source naming no reference: `gitHubApp` (`configured`, `appId`, `keySource` (a variable name, a path or a reference, never the key), `apiUrl`, `host`, `problem`) and `hostToken` (`configured`, `username`). It is this host's environment; each worker reads its own.
- `POST /repos/credentials/github-app/check` (operate): asks GitHub, as the host's App, for the App's `name`, `slug`, `owner` and `htmlUrl` and its `installations` (`id`, `account`, `accountType`, `repositorySelection`, `suspendedUtc`; at most 100, `moreInstallations` when there are more). 409 when the host has no App or a misconfigured one, 400 `GitHub App check failed` when GitHub refuses it (the message says what to check).
- `POST /repos/sources/{id}/proposals` (author): propose pipelines to the source's repo as a pull request. Body `{title, body?, baseBranch?, headBranch?, files:[{path, content}]}`; `baseBranch` defaults to the source's tracked branch and `headBranch` to a content-derived `sqlflow/proposal-*` name. The control plane pushes the files onto a fresh branch off the base and opens a pull request using the source's own stored credential (github.com or bitbucket.org over HTTPS); it never writes the catalog directly, so a human reviews and merges before the managed sync imports the flows. `files[].path` must be repo-relative and end in `.yaml`/`.yml`/`.sql`/`.json`/`.md`; a bad path, an unsupported remote host, or a source with no configured credential is a 400. Returns 201 with `{pullRequestUrl, pullRequestNumber, headBranch, commitSha, filesChanged}`. Feed `commitSha` to `POST /runs` (`commitSha`) to test the proposal pinned to the pull-request commit before it merges. If the branch pushes but the pull request cannot be opened, the branch is rolled back and the response is 502. The git clone is staged in a throwaway temp directory deleted when the request finishes; the whole staging root (`{temp}/sqlflow/proposals`) is also swept on control-plane startup and shutdown, so a staged clone never outlives the session and a crash leak is reclaimed on the next start.

### What a repository holds, and its page

`GET /repos/{id}/tree` (read, src/SqlFlow.ControlPlane/Api/RepoTreeEndpoints.cs) lists every folder and file a repository holds, not only the flow files a sync imported: repo-relative, forward-slashed paths sorted by path, whether each is a folder, and each file's size, with `readFrom` (`git` or `disk`) and `truncated`. No file's content is served here. A repo tracked by a git source is read at its branch tip through the shared read-only clone, refreshed at most once a minute. A repo synced by the CLI from a local path is read from that folder on disk, every file in it, when the control plane's host can see it. A repo with neither answers 400 saying why. Git's own files (`.git`, `.gitignore`, `.gitattributes`, `.gitmodules`, `.gitkeep`) are left out at any depth, and a listing stops at 20,000 entries with `truncated` set.

A repo's page in the GUI lists its projects (its top-level folders) from this listing, merged with the pipelines the catalog imported. A project starts collapsed, and opening it shows its top-level folders and the rows directly in it, every folder shut: a folder opens one level at a time, so a repository that keeps thousands of files beside its flows (a data drop, a folder per well) never unfolds them on arrival. A search opens each project it matched onto the folders on the way to the rows it matched; a project matched by its own name is kept whole, its folders shut. The Pipelines page lays out a repo's flows the same way, and a filter there opens the folders on the way to its matches.

`GET /repos/{id}/file?path=<path>` (read, src/SqlFlow.ControlPlane/Api/RepoFileEndpoints.cs) is one file of the repository, read where the listing reads it: its `path`, `readFrom` (with the `branch` and `commitSha` it was read at, for a git source), `sizeBytes`, and its `text`. A text file is decoded as UTF-8 unless a byte order mark says otherwise, and past its first megabyte (1,048,576 bytes) only that much is returned, with `truncated` set. A file with a NUL byte among its first 8,000 bytes is `binary`, and has no text. The route shows what a repository is for, and refuses the rest, saying why:

| Asked for | Answer |
| --- | --- |
| A path that is not repo-relative and forward-slashed, names a folder by `.`, `..` or an empty name, or names git's own files | 400 `Not a repository file` |
| A file in a local-path repo reached through a link (the file or a folder on the way), which may lead out of the repository | 400 `Not a repository file` |
| A folder, or a submodule | 400 `Not a file` |
| A credential store by its name: an environment file (`.env`, `.env.*`), a login or key file (`.npmrc`, `.pypirc`, `.netrc`, `.pgpass`, `.git-credentials`, `id_rsa` and the other SSH keys), or a key or certificate store (`.pem`, `.key`, `.pfx`, `.p12`, `.jks`, `.keystore`, `.kdbx`) | 403 `Content not shown` |
| An untracked file git ignores in a local-path repo that is a git working tree: it never reaches the repository, and is where credentials, data drops and build output sit | 403 `Content not shown` |
| A file the repository does not hold, or an unknown repo | 404 `Not found` |

The route is closed to an assistant run's delegated token, which reads the estate's metadata: a file's text is the repository's own content.

On a repo's page a click on a flow or a file, or Enter on its row, opens the **code window**: the file's code as the repository holds it, coloured by its kind (YAML, JSON, SQL, Markdown, scripts and configuration; anything else as text), with what it is, its size and where it was read above it. A flow document or a module's document (a YAML file declaring `flowType` or `documentType`) gets the YAML editor's documentation, colouring and checks. The window slides in from the left over the page, as Explain does: its right edge is dragged (or moved with the arrow keys) to make it wider or narrower, the width is kept for the next time, and it maximizes to read the code alone. A flow's file offers **Open pipeline**, the flow's page; a file that is not shown says why. The window belongs to the page it was opened on, and Explain, asked of its code, opens over it.

## Triggering, backfilling, and cancelling runs

`POST /api/v1/runs` (operate scope, src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs) takes:

```json
{
  "repoId": "0195c9a2-7f30-7c44-9c1e-0aa1b2c3d4e5",
  "flowName": "ing/customers",
  "pool": null,
  "commitSha": null,
  "fullLoad": false,
  "backfillFrom": "2026-01-01",
  "backfillTo": "2026-01-31",
  "filePattern": null,
  "scope": null,
  "batch": null,
  "assertionsOnly": false
}
```

- `flowName` is required and must be non-blank (400 "A run trigger requires a non-blank flowName.").
- `commitSha` must be a 4 to 64 character hexadecimal git object id, or the request is 400: "commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit)." When omitted, enqueueing pins the run to the repo's last synced commit (`LastSyncedSha`) when one is resolvable, so the executed version matches what the catalog shows and any node can materialize it; only a repo with no resolvable synced commit runs unpinned from the node's local copy (src/SqlFlow.Catalog/RunQueueStore.cs).
- The per-run substitution parameters (`fullLoad`, `backfillFrom`/`backfillTo`, `filePattern`, `sourceFilter`) are validated at the trust boundary via `RunParameters.Validate()`, so an inverted window or a control-character glob is refused as 400 "Invalid run parameters" before anything is queued. They then pass the pipeline's kind through the loader's one rule (`YamlDocumentLoader.ValidateRunParameters`): a flow of a registered kind (one a host module adds) has its kind check every run, so an override the kind does not apply is refused 400 with the kind's own message rather than queued and ignored. A registered kind's `operation`, `values` and `payload` are checked the same way, by its kind; the values of the parameters a flow's document declares are resolved when the run starts, by the one rule a run form and a schedule's preview explain them with ([Run inputs](run-inputs.md#the-one-rule)), and a value the document refuses fails the run then.
- An unknown or deactivated pipeline is 404: "No active pipeline '&lt;flow&gt;' in repo '&lt;repoId&gt;'." The pipeline's kind is carried onto the queued run.
- `scope` selects how much to run: omitted or `flow` triggers the single flow (the default described above); `node` expands the named flow and all its lineage descendants into one wave-gated run group. Any other value is 400; a whole data source runs through its schedule (`POST /schedules/{id}/run`). A node run takes only a backfill window: its anchor reads the window, every relational (`ing`) descendant re-reads from the source minimum, and the rest run as defined, each member's parameters passing its own kind's check first (a refusal is 400 naming the member). `fullLoad`, `filePattern`, `sourceFilter`, `assertionsOnly` and kind arguments are single-flow concepts, so a node scope carrying one is refused 400 rather than run without it.
- `assertionsOnly` (ingestion flows only) evaluates the flow's data-quality assertions, manual-mode ones included, against the current target without loading anything; on any other flow kind it is a 400.
- Success for a single flow is 202 Accepted with `{runId, status: "queued"}` and a `Location` header pointing at `GET /api/v1/runs/{runId}`.
- Success for a `node` scope is 202 Accepted with a `RunGroupAccepted` payload `{groupId, memberCount, status: "queued"}` and a `Location` header pointing at `GET /api/v1/runs/groups/{groupId}`. A scope that expands to no active members is 404.
- No secret is ever accepted in the request or echoed back; the executing node resolves all credentials from its own environment.

`POST /api/v1/runs/{runId}/cancel` (operate scope): a still-queued run is cancelled outright, 200 `{status: "cancelled"}`; a run already RUNNING is cancellable too, but asynchronously, so its owning node is stamped with a durable cancel request and the endpoint returns 202 Accepted with `{status: "cancelling"}` (poll `GET /api/v1/runs/{runId}` for the terminal outcome). An unknown id is 404 "No run '{runId}'."; an already-terminal run is 409 "Run '{runId}' has already finished and cannot be cancelled." The companion `POST /api/v1/runs/groups/{groupId}/cancel` cancels a whole run group the same way (queued members cancelled outright, running members sent a request): 202 `{status: "cancelling"}` when any member was still running, 200 `{status: "cancelled"}` otherwise, and 404 for an unknown group.

## Lineage read API

src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs serves the catalog's lineage graph:

- `GET /lineage/schemas` and `GET /lineage/schemas/kinds`: the (server, database, schema) groupings of the catalog's database objects with how many objects each holds, and their per-kind breakdown, unpaged (bounded by the estate's schema count, not its object count). Names that differ only in case are one grouping, as they are one node key, shown in the spelling most of the grouping's objects carry; a null database or schema is an object whose identity a sync could only partly resolve. Filters: `serverRef`, `database` and `schema`, compared without regard to case, and `databaseUnknown=true` / `schemaUnknown=true` for the objects whose database or schema is unknown (a request naming a database and asking for an unknown one at once is a 400; likewise for a schema). The shared rule is src/SqlFlow.ControlPlane/Api/CatalogBrowse.cs.
- `GET /lineage/objects`: paged object list with `name` (contains), `kind`, and the same `serverRef`, `database`, `schema`, `databaseUnknown` and `schemaUnknown` filters as the groupings, so a grouping's objects are exactly the ones it counted; ordered by `level` (the object's depth in the estate-wide data-movement graph, sources first; null level last), then database/schema/name. `ObjectDto` deliberately omits the heavy `Definition` field.
- `GET /lineage/objects/detail?key=...`: one object with its full module body; `definition` is null for plain tables, an unconnected sync, or an encrypted module.
- `GET /lineage/objects/columns?key=...`: paged columns of one object, ordered by ordinal.
- `GET /repos/{repoId}/lineage/edges`: paged attributed edges with `pipelineId`, `objectKey`, `relation`, and `tier` filters.
- `GET /repos/{repoId}/waves`: the execution plan grouped by wave, whole (not paged); wave `-1` means lineage has not been computed for those pipelines yet.
- `GET /repos/{repoId}/dependencies`: paged flow-to-flow dependencies.
- `GET /pipelines/{id}/columns` (src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs): the declared and detected pre-ingestion transform columns, declared-first.

Every query is read-only (`AsNoTracking`), projected to DTOs (raw EF entities never leave the host), and listing endpoints are bounded by page size. Repo-scoped endpoints verify the repo exists and return 404 for an unknown repo rather than an empty result a caller could not distinguish from a repo with no lineage yet.

## Configuration touchpoints

- Configuration section: `ControlPlane` (env form `ControlPlane__...`), documented in the table above; src/SqlFlow.ControlPlane/appsettings.json carries the non-secret defaults.
- Environment variables: `SQLFLOW_CATALOG_DB` (the default catalog connection reference), `SQLFLOW_GITHUB_APP_ID`, `SQLFLOW_GITHUB_APP_PRIVATE_KEY`, `SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH` and `SQLFLOW_GITHUB_APP_API_URL` (the host's GitHub App), `SQLFLOW_GIT_TOKEN` and `SQLFLOW_GIT_USERNAME` (managed-sync and worker git credentials), `SQLFLOW_URL` and `SQLFLOW_TOKEN` (a standalone worker's control plane and node token).
- CLI: `sqlflow db sync [path] [--repo <name>] [--repo-url <url>] [--connect]` runs the same `CatalogSync` a managed source runs; `sqlflow worker` runs the same node runtime the control plane hosts in-process, over HTTP instead of in-process (src/SqlFlow.Cli/Program.cs).

## Example: compose deployment

Adapted from deploy/compose/docker-compose.yml (API-only control plane replica plus separate workers):

```yaml
services:
  controlplane:
    environment:
      ControlPlane__Catalog__ConnectionReference: "Server=mssql;Database=SqlFlowCatalog;User ID=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
      ControlPlane__Jwt__SigningKey: ${SQLFLOW_JWT_SIGNING_KEY}
      ControlPlane__Jwt__BootstrapSecret: ${SQLFLOW_BOOTSTRAP_SECRET}
      ControlPlane__Bootstrap__AdminUsername: ${SQLFLOW_ADMIN_USERNAME:-admin}
      ControlPlane__Bootstrap__AdminPasswordReference: ${SQLFLOW_ADMIN_PASSWORD}
      ControlPlane__Cors__AllowedOrigins__0: http://localhost:8081
      ControlPlane__Worker__Enabled: "false"
    ports:
      - "5000:8080"

  worker:
    environment:
      SQLFLOW_URL: http://controlplane:8080
      SQLFLOW_TOKEN: ${SQLFLOW_NODE_TOKEN}
      SQLFLOW_CATALOG_DB: "Server=mssql;Database=SqlFlowCatalog;User ID=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
      SQLFLOW_GIT_TOKEN: ${SQLFLOW_GIT_TOKEN:-}
```

Register a source and trigger a run:

```bash
# Register a managed repo source (operate scope)
curl -s -X POST http://localhost:5000/api/v1/repos/sources \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"warehouse","remoteUrl":"https://github.com/acme/warehouse-flows.git","branch":"main","syncIntervalSeconds":300}'

# Trigger a backfill run (202 Accepted; poll the Location header)
curl -s -X POST http://localhost:5000/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"repoId":"'$REPO_ID'","flowName":"ing/customers","fullLoad":false,"backfillFrom":"2026-01-01","backfillTo":"2026-01-31"}'
```

## See also

- concept-data-operations: the duplicate-key check and the baseline comparison behind `DataOps:Enabled`


- [Worker CLI](../cli/worker.md): the standalone node runtime that drains the same run queue.
- [Authentication and identity](./authentication-and-identity.md): tokens, local users, Entra SSO, roles.
- [The shadow catalog](./shadow-catalog.md): the database this API reads and the sync writes.
- [Deployment guide](../guides/deployment.md): compose and ingress layouts.
