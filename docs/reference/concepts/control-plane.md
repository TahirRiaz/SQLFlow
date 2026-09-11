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
  - concept-authentication-and-identity
  - concept-shadow-catalog
  - guide-deployment
sourceRefs:
  - src/SqlFlow.ControlPlane/Program.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Infrastructure/CatalogConnectionProvider.cs
  - src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncService.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RepoSourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
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
---

# The control plane: API surface, configuration, bootstrap, managed sync

The control plane (src/SqlFlow.ControlPlane) is an ASP.NET Core host that serves the HTTP API over the shadow catalog, triggers and queues runs, fires schedules, and keeps the catalog synced from git. It composes the identical engine wiring as the CLI and standalone workers via `AddSqlFlowEngine()` (src/SqlFlow.ControlPlane/Program.cs), so a run triggered over the API behaves exactly like a run started by `sqlflow run`: same source readers, same secret resolution chain, same connection registry, same `DocumentExecutor`.

It exists so that a team has one place to observe the estate (repos, pipelines, runs, lineage, nodes), one privileged surface to operate it (trigger, cancel, schedule, sync), and zero manual steps to keep the queryable shadow catalog current with git.

## Host composition

`src/SqlFlow.ControlPlane/Program.cs` builds the host in one pass:

- Configuration is bound from the `ControlPlane` section and validated eagerly at startup (`options.Validate()` plus `ValidateOnStart()`), so a misconfigured deployment never starts serving.
- The catalog is read through a pooled `CatalogDbContext` with `NoTracking` query behavior and `EnableRetryOnFailure`.
- Background services host bootstrap provisioning (`BootstrapProvisioningService`), the dispatcher (`DispatchService`), the in-process node (`RunExecutionWorker`, only when `Worker:Enabled` is true), the schedule scanner (`SchedulerService`), and the managed git sync (`RepoSyncService`).
- The run queue lives in this process: the trigger endpoint journals a `queued` row through `IRunDispatcher` and tells the in-memory dispatcher, which hands work to nodes that poll for it and journals every placement with plain conditional updates (see "The dispatcher" below). Runs survive a restart (the dispatcher rebuilds from the journal) and are visible to the read API from the moment they are queued.
- Every response passes through `CorrelationIdMiddleware`, and errors are surfaced as RFC problem details (`AddProblemDetails` plus a `GlobalExceptionHandler`).
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
| `GET /pipelines/{id}/columns` | Declared and detected pre-ingestion transform columns |
| `GET /pipelines/{id}/files`, `GET /pipelines/{id}/files/stats` | Every distinct file the pipeline has processed (deduplicated by name and path), and the size profile over them: average, median, standard deviation, extremes, totals, and a window over the newest files, so a caller can tell a normal delivery from an anomalous one |
| `GET /runs`, `GET /runs/{runId}` | Run history and detail (filters include `repoId`, `pipelineId`, `flowKind`, `status`, `success`, `flowName`, `batch`, `latest`) |
| `GET /runs/{runId}/files` `/assertions` `/statements` `/surrogate-keys` `/health-metrics` | Per-run drill-down |
| `GET /runs/{runId}/trace` | The run's consolidated trace: canonical engine events (file progress, resolved watermarks, decisions, stage summaries, warnings) interleaved with the generated SQL statements by timestamp |
| `GET /runs/{runId}/trace/text` | The whole trace rendered as one plain-text document (run header, then every entry with SQL inline): the Copy-trace surface, and the form to hand an LLM when debugging a run |
| `GET /runs/{runId}/trace/stream` | The trace as Server-Sent Events while the run executes: one `entry` frame per new trace entry, a final `end` frame at the terminal status (the client then refetches the paged endpoint, which holds the authoritative re-projection); `afterEventId`/`afterStatementId` cursors resume a dropped connection |
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
| `GET /nodes` | Worker fleet with a derived online flag |
| `GET /repos/sources` | Managed repo sources |
| `GET /summary` | Estate summary |
| `GET /dataops/capabilities` | Whether the data-operations surface is enabled here, the operations available, and the linked servers a baseline comparison may name (concept-data-operations) |

Operate surface (policy `operate`):

| Endpoint | Purpose |
| --- | --- |
| `POST /runs` | Trigger a run or a run group (202 Accepted) |
| `POST /runs/{runId}/cancel` | Cancel a run (200 `cancelled` from queued; 202 `cancelling` while running) |
| `POST /runs/groups/{groupId}/cancel` | Cancel a whole run group (queued members cancelled, running members requested) |
| `POST /schedules`, `POST /schedules/{id}/pause`, `POST /schedules/{id}/resume`, `DELETE /schedules/{id}` | Ad-hoc schedule management |
| `POST /repos/sources` | Register or update a managed repo source (upsert) |
| `POST /repos/sources/{id}/sync` | Force a sync now |
| `POST /repos/discover` | Preview a repo's flows without importing (the selective-scan wizard) |
| `POST /repos/sources/{id}/proposals` | Propose pipelines to a source as a pull request (`author` scope) |
| `POST /datasources/tasks` | Enqueue an ad-hoc compute task against a datasource: browse/introspect, detect a unique key, the warehouse-health DMV probes, and (behind `DataOps:Enabled`) `duplicateKeys`, `compareBaseline` and `runQuery`. References only; the executing node resolves the credential |
| `POST /datasources/tasks/{taskId}/cancel` | Cancel a queued or running compute task |
| `POST /dataops/queries/prepare` | Validate an ad-hoc SELECT and mint a one-time plan token; nothing runs. Behind `DataOps:Enabled` |
| `POST /dataops/queries/{planId}/run` | Redeem an approved plan token and queue the query. Takes a token, never a statement, so the confirmation cannot be skipped |

Admin surface (policy `admin`): `GET/POST /users`, `POST /users/{id}/role` `/activate` `/deactivate` `/password`, and `GET /roles` (src/SqlFlow.ControlPlane/Api/UserEndpoints.cs).

### Health probes and rate limiting

- `GET /health/live` has no dependencies (its predicate excludes every check); `GET /health/ready` runs the catalog database check. Both are mapped with `DisableRateLimiting()`, so probe traffic behind a shared egress IP can never be throttled into a false negative.
- A global fixed-window rate limiter partitions per authenticated subject (the `sub` claim) or, when anonymous, per client IP: default 120 permits per 60 second window, `QueueLimit = 0`, and HTTP 429 on rejection. The limiter runs after authentication so the partition can key on the subject.

### CORS and reverse-proxy awareness

- CORS is an explicit allowlist (`ControlPlane:Cors:AllowedOrigins`). An empty list means no CORS policy is registered at all: same-origin only.
- Forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) are honored only when `ControlPlane:Proxy:Enabled` is true, and only from the proxies listed in `KnownNetworks` (CIDRs) or `KnownProxies` (IPs), with `ForwardLimit` hops (default 1). The forwarded-headers middleware runs first in the pipeline so the rate limiter and login throttle see the real client address. Enabling the proxy section while trusting no proxies is a startup error ("ControlPlane:Proxy is enabled but trusts no proxies; ...").

### Fleet visibility

`GET /nodes` (src/SqlFlow.ControlPlane/Api/NodeEndpoints.cs) lists workers the dispatcher has heard from, most recently seen first. A node is reported `online` when its last poll is within the last 60 seconds, computed at read time. The dispatcher keeps the fleet in memory and flushes each node's last poll (build, pool, `BusyRuns`) to the catalog's `Node` table every `Dispatch:NodeFlushSeconds`, so a fleet of hundreds costs the catalog a few dozen updates a minute rather than one per heartbeat; `BusyRuns` is what the KEDA autoscaler reads so occupied workers hold their replicas while idle ones remain the reclaimable surplus. `GET /dispatch` returns the dispatcher's own view: every queued run with the gate holding it back (`pipeline-busy`, `wave-gated`, `group-cap`, `no-eligible-node`), every lease with its node and expiry, the fleet, ownership, and the last housekeeping passes.

### The dispatcher

The dispatcher (`Dispatcher` in src/SqlFlow.Dispatch/Dispatcher.cs, hosted by `DispatchService` in src/SqlFlow.ControlPlane/Dispatch/DispatchService.cs) holds every queued and executing run and compute task in memory and makes each placement decision under one lock: an untargeted run goes to any node and a pooled run only to a node serving that pool; a run group member is eligible only when every lower wave is terminal and while fewer than its group's cap are executing; and no two runs of one pipeline ever execute at once, because each flow stages through one canonical work table. Eligible runs are handed out oldest first, and a run a gate holds back never blocks a later run that is eligible.

Memory is authoritative for placement; the catalog is the journal. An enqueue writes the `Run` row before the dispatcher learns of it; a hand-out is reserved in memory, journaled (`queued` to `running`, the attempt advanced, conditional on the row still being queued at that attempt), and only then handed to the node; an outcome is journaled under the fence and then dropped from memory. Nothing in that journal is more than a plain conditional `UPDATE`, so no locking hint or provider-specific feature is involved. At activation the dispatcher rebuilds from the journal: queued rows are queued, running rows are leased to their recorded node under a grace lease so a node still executing them reattaches on its next poll. A reconcile pass every `Dispatch:ReconcileSeconds` diffs memory against the journal and heals anything that bypassed the in-process notify (a run cancelled straight against the catalog by `sqlflow runs cancel --db`, an enqueue on a passive replica), acting on additions at once and on removals only when they persist across two passes.

Nodes pull work through the node protocol under `/api/v1/node` (src/SqlFlow.ControlPlane/Dispatch/NodeProtocolEndpoints.cs), authenticated with the `node` scope, and it is the only connection a node has: a node never opens a catalog connection. `POST /node/poll` is the heartbeat, the lease renewal, the cancel channel and the hand-out in one long-polled call (held for up to `Dispatch:LongPollSeconds` when nothing is available, answered the instant something is); each handed-out run carries its execution spec (the repo and pipeline identity, the remote and root path, the commit pin, the flow-version hash, the run parameters, the git credential reference), which the dispatcher reads from the catalog before it journals the hand-out, so a read that fails leaves the run queued with no attempt consumed. While a run executes, `GET /node/flow-versions/{hash}` serves the snapshotted YAML the enqueue staged (content-addressed, so a node fetches each version once), `POST /node/runs/{runId}/context` resolves the lineage facts the run depends on (the downstream watermark table, the landing-reset verdict; src/SqlFlow.Catalog/RunContextStore.cs) once the node has parsed the document and knows the flow participates, and `POST /node/runs/{runId}/trace` takes the run's generated SQL and canonical events in batches (every 250 ms, 200 items or 2 MB of text, whichever first; src/SqlFlow.Catalog/RunTraceStore.cs) so the Statements and Events views stream while the run executes. `POST /node/runs/{runId}/outcome` and `POST /node/tasks/{taskId}/outcome` report results. Every hand-out is a lease (`Dispatch:LeaseSeconds`, default 90) renewed by each poll that reports the run held; a lapsed lease is dispositioned exactly as a dead node's runs always were: requeued while attempts remain (`MaxExecutionAttempts`, 3; the interrupted attempt's live trace rows are discarded with it, so the run's trace is the successor's), recorded `cancelled` when an operator cancel was already pending, failed once the budget is exhausted (its group dependents skipped). Every hand-out increments the run's `Attempt`, which is the fencing token every per-run call presents: a node whose lease lapsed and whose run was handed out again presents a stale attempt on its context request, its trace batches and its outcome report, each of which is refused (checked in the dispatcher's memory first and in the journal again), so the successor execution's rows and result are authoritative. The node protocol has its own rate-limit partition (`RateLimit:NodePermitPerWindow`), keyed by the token's subject plus the node name every call carries in the `X-SqlFlow-Node` header, so a fleet of hundreds sharing one node token still gets a window per node and never trips the per-user limit meant for people.

Exactly one replica dispatches at a time. `DispatchService` acquires the `DispatchLease` row (src/SqlFlow.Catalog/DispatchLeaseStore.cs) with one conditional update, renews it every `Dispatch:OwnershipRenewSeconds`, releases it on a graceful stop so a revision swap hands over within one renew interval, and deactivates the dispatcher if the lease cannot be renewed before its TTL. A replica that does not own dispatch still serves the API and journals enqueues (the owner's reconcile picks them up); its node routes answer 503 with `Retry-After: 2`, and a node's client retries until it lands on the owner. In a single-app deployment the control plane is the only replica and always the owner.

A **stopping** node is not a dead one, and the two must not be confused. When a worker is asked to stop (a SIGTERM from an autoscaler reclaiming the replica, a revision swap, an operator restart) it stops taking work but keeps executing the runs it already holds, and it keeps polling with no free slots for the whole drain precisely so its leases stay alive (src/SqlFlow.Node/RunWorker.cs). Were a draining node to go silent, its leases would lapse, its runs would be requeued, and a sibling node would re-execute work that was about to finish while the draining node's own outcome reports lost to the fence. Only when a node's drain window expires does it sever what remains, and those runs' leases then lapse for the dispatcher to requeue.

Because a requeue consumes an execution attempt, anything that severs runs routinely will eventually exhaust the budget of a healthy run and fail it with "a run that repeatedly dies mid-flight is treated as the cause". In an autoscaled fleet the usual culprit is not the flow but the platform: replicas are reclaimed on a cadence, and a termination grace period shorter than the worker's drain window turns every scale-in into a severed run. See [`sqlflow worker`](../cli/worker.md) for the drain contract and the grace-period requirement.

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
| `Bootstrap:DemoRepo` | unset | `Name` and `RemoteUrl` required when configured; `Branch` default `main`; `SyncIntervalSeconds` default `300` |
| `Cors:AllowedOrigins` | `[]` | Empty means same-origin only |
| `RateLimit:PermitPerWindow` / `RateLimit:WindowSeconds` | `120` / `60` | Both must be positive |
| `RateLimit:NodePermitPerWindow` | `6000` | The per-node window for the node protocol, keyed by the token subject plus the node name each call carries; must be positive |
| `Scheduler:PollSeconds` | `15` | Schedule scan cadence; must be positive |
| `ManagedSync:PollSeconds` | `30` | Repo-source scan cadence |
| `Worker:Enabled` | `true` | `false` makes the replica API-only (no in-process node) |
| `Worker:Pools` | `[]` | Empty takes only untargeted runs |
| `Worker:MaxConcurrentRuns` / `Worker:MaxConcurrentComputeTasks` | `4` / `2` | The in-process node's slots; both at least 1 |
| `Dispatch:LeaseSeconds` | `90` | How long a hand-out lasts without a renewing poll; at least twice `LongPollSeconds` |
| `Dispatch:LongPollSeconds` | `30` | The longest a node's poll is held open; 1 to 60 |
| `Dispatch:ReconcileSeconds` | `5` | How often memory is diffed against the journal |
| `Dispatch:NodeFlushSeconds` | `5` | How often the fleet registry is flushed to the catalog |
| `Dispatch:NodeRetentionHours` | `24` | How long a silent node stays in the registry; `0` never prunes |
| `Dispatch:TaskQueuedExpiryMinutes` / `Dispatch:TaskRunningExpiryHours` | `15` / `6` | When an unclaimed or overlong compute task is failed with a hint |
| `Dispatch:MaxExecutionAttempts` | `3` | Hand-outs a run may consume before an interrupted attempt is failed |
| `Dispatch:OwnershipTtlSeconds` / `Dispatch:OwnershipRenewSeconds` | `30` / `10` | The dispatch ownership lease; the TTL is at least twice the renew interval |
| `Proxy:Enabled` | `false` | See proxy section above; `ForwardLimit` default `1` |
| `DataOps:Enabled` | `false` | The kill switch for the data-operations surface: ad-hoc business queries, the duplicate-key check and the old-versus-new baseline comparison. All are read-only, and both are refused with a 403 naming this setting while it is off. The four warehouse-health DMV probes are a separate, older feature and are NOT gated by it (concept-data-operations) |
| `DataOps:Comparison:LinkedServers` | `[]` | The linked servers a baseline comparison may name. A linked-server name becomes an identifier in generated SQL and a route into another estate, so it is configuration, never something a request chooses; an unlisted name is refused |
| `DataOps:Comparison:DefaultLinkedServer` | unset | Used when a comparison names none; must be one of `LinkedServers` or startup fails |
| `DataOps:Comparison:Databases` | `[]` | Optional narrowing to named databases on those servers. Empty permits any database the linked server's own login can reach, which is the usual case because the linked server is the boundary |

## First-run bootstrap

`BootstrapProvisioningService` (src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs) runs in the background and retries with backoff (5s, 10s, 20s, 40s, then every 60s) until the catalog is reachable, so the start order of app and database never matters; the readiness probe reports the catalog being unavailable in the meantime.

Before provisioning anything, the service applies the `Bootstrap:AllowCreate` guard (default `false`). Unless it is `true`, a missing database or a populated non-catalog database is a deterministic configuration mistake that retrying cannot fix, so the service logs a critical error and stops (readiness stays red) rather than conjuring a database or injecting catalog tables into the wrong server. Only `AllowCreate = true` creates the catalog database and initialises its schema into an empty one.

Order of operations, all idempotent:

1. Apply pending EF catalog migrations when `Bootstrap:ApplyMigrations` is true (the default). With it false, pending migrations are logged as a warning: "Apply them out of band; parts of the API may fail until then."
2. Seed the built-in roles: `admin` (scopes `read operate admin author node`), `operator` (`read operate author`), `viewer` (`read`). Seeding ensures existence and guarantees the code-defined scopes are present: a scope an operator added to a built-in role survives re-seeding, and a scope the definition gained later (such as `node`) is appended to an existing row, so an older catalog never needs it granted by hand (`EnsureRoleAsync`).
3. Create the initial admin from `Bootstrap:AdminUsername` and `Bootstrap:AdminPasswordReference` (resolved through the secret resolver) when the user is absent. An existing admin's password is never reset. An empty or too-short resolved password logs an error and skips creation rather than provisioning a weak credential.
4. Register the optional demo repo source (`Bootstrap:DemoRepo`) as an upsert, so configuration stays the desired state across restarts.

## Managed git-to-catalog sync (repo sources)

A `CatalogRepoSource` (src/SqlFlow.Catalog/CatalogEntities.cs) registers a git repository the control plane keeps synced: it periodically pulls the branch HEAD and runs the catalog sync, so nobody runs `sqlflow db sync` by hand and git stays the source of truth. Defaults: `Branch` `main`, `SyncIntervalSeconds` `300`, `Enabled` `true`. `Name` is unique and the synced pipelines and runs are attributed to it. A source's id is `FlowIdentity.FromName("reposource/{name}")`, so re-registering the same name updates in place; a new source is due immediately (`NextSyncUtc = now`).

`RepoSyncService` (src/SqlFlow.ControlPlane/Background/RepoSyncService.cs) scans every `ManagedSync:PollSeconds` (default 30, at most 50 sources per tick) for sources due to sync. Each due sync is claimed by compare-and-swap on `NextSyncUtc` (`RepoSourceStore.TryClaimSyncAsync`), so several control-plane nodes never sync the same source twice in one interval. The winner then:

1. Clones the branch tip fresh into `<cache>/<repoHash>/branch` (`GitMaterializer.MaterializeBranch`, src/SqlFlow.Node/GitMaterializer.cs). A repo with no commits fails with `'<remote>' (branch '<branch>') has no commits to sync.`
2. Runs the exact same `CatalogSync` as the CLI's `sqlflow db sync` (the offline tier; the connected/derived tier is a separate opt-in of the CLI's `--connect`), passing the source's flow selection (see below).
3. Records the pulled commit SHA on success (`LastSyncedSha`, clearing `LastError`) or a secret-redacted error on failure (`LastError`, keeping the last successful SHA).

One source's failure never stops the others or the loop.

### Git credentials (a reference, never a secret)

A private remote's token is named by the source's `CredentialReference`, a `${keyvault:vault/secret}` or `${env:NAME}` reference resolved through the SqlFlow secret resolver at clone time (`GitMaterializer.ResolveCredentialsAsync`); an optional `CredentialUsername` pairs with it for hosts that authenticate the username too (Bitbucket app passwords). Only the reference is stored; the secret value is created and maintained in the vault, and the API rejects a raw token that is not a `${...}` reference. A source with no reference falls back to the host's own environment (`SQLFLOW_GIT_TOKEN`, optional `SQLFLOW_GIT_USERNAME`), which covers public remotes. The node resolves the secret itself and it never rests in the catalog or the queue.

### Preview-first selective scan

A source imports every `*.flow.yaml` by default. To onboard a subset, `POST /repos/discover` clones the remote and lists its flows (relative path, parsed name and kind or the parse error, size, and redacted content for preview) WITHOUT touching the catalog. The chosen exclusions are stored on the source as `ExcludedFlowPaths` (a JSON array of repo-relative paths); the sync then projects only the included flows as pipelines. Because the sync is the single activation point, an excluded flow never becomes a catalog pipeline (so the scheduler never runs it), a previously-imported flow that becomes excluded is deactivated on the next sync (its run history is kept), and a re-included flow is imported again.

API:

- `GET /repos/sources` (read): paged list of `RepoSourceDto` with fields `id`, `name`, `remoteUrl`, `branch`, `enabled`, `syncIntervalSeconds`, `nextSyncUtc`, `lastSyncUtc`, `lastSyncedSha`, `lastError`, `credentialReference`, `credentialUsername`, `excludedFlowPaths`, `createdUtc`, `updatedUtc`.
- `POST /repos/sources` (operate): upsert with body `{name, remoteUrl, branch?, syncIntervalSeconds?, enabled?, credentialReference?, credentialUsername?, excludedFlowPaths?}`; `branch` defaults to `main`, `syncIntervalSeconds` to 300, `enabled` to true, and an omitted `excludedFlowPaths` imports every flow. A blank `name` or `remoteUrl`, or a `credentialReference` that is not a `${...}` reference, is 400. Returns 201 Created with the source id.
- `POST /repos/sources/{id}/sync` (operate): makes the source due now; 404 for an unknown or disabled source ("No enabled repo source '{id}'.").
- `POST /repos/discover` (operate): body `{remoteUrl, branch?, credentialReference?, credentialUsername?}`; clones and returns the flow list for the selection wizard. Read-only; nothing is written to the catalog. A clone or credential failure is a 400 with a redacted message.
- `POST /repos/sources/{id}/proposals` (author): propose pipelines to the source's repo as a pull request. Body `{title, body?, baseBranch?, headBranch?, files:[{path, content}]}`; `baseBranch` defaults to the source's tracked branch and `headBranch` to a content-derived `sqlflow/proposal-*` name. The control plane pushes the files onto a fresh branch off the base and opens a pull request using the source's own stored credential (github.com or bitbucket.org over HTTPS); it never writes the catalog directly, so a human reviews and merges before the managed sync imports the flows. `files[].path` must be repo-relative and end in `.yaml`/`.yml`/`.sql`/`.json`/`.md`; a bad path, an unsupported remote host, or a source with no configured credential is a 400. Returns 201 with `{pullRequestUrl, pullRequestNumber, headBranch, commitSha, filesChanged}`. Feed `commitSha` to `POST /runs` (`commitSha`) to test the proposal pinned to the pull-request commit before it merges. If the branch pushes but the pull request cannot be opened, the branch is rolled back and the response is 502. The git clone is staged in a throwaway temp directory deleted when the request finishes; the whole staging root (`{temp}/sqlflow/proposals`) is also swept on control-plane startup and shutdown, so a staged clone never outlives the session and a crash leak is reclaimed on the next start.

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
- The per-run substitution parameters (`fullLoad`, `backfillFrom`/`backfillTo`, `filePattern`) are validated at the trust boundary via `RunParameters.Validate()`, so an inverted window or a control-character glob is refused as 400 "Invalid run parameters" before anything is queued.
- An unknown or deactivated pipeline is 404: "No active pipeline '&lt;flow&gt;' in repo '&lt;repoId&gt;'." The pipeline's kind is carried onto the queued run.
- `scope` selects how much to run: omitted or `flow` triggers the single flow (the default described above); `node` expands the named flow and all its lineage descendants; `batch` expands a whole data source, identified either by the `batch` label or by reading it from the anchor `flowName`. The built-in backfill and `assertionsOnly` are single-flow concepts, so a `node`/`batch` scope always runs its members with default parameters (a group carrying `assertionsOnly` is refused 400).
- `assertionsOnly` (ingestion flows only) evaluates the flow's data-quality assertions, manual-mode ones included, against the current target without loading anything; on any other flow kind it is a 400.
- Success for a single flow is 202 Accepted with `{runId, status: "queued"}` and a `Location` header pointing at `GET /api/v1/runs/{runId}`.
- Success for a `node`/`batch` scope is 202 Accepted with a `RunGroupAccepted` payload `{groupId, memberCount, status: "queued"}` and a `Location` header pointing at `GET /api/v1/runs/groups/{groupId}`. A scope that expands to no active members is 404.
- No secret is ever accepted in the request or echoed back; the executing node resolves all credentials from its own environment.

`POST /api/v1/runs/{runId}/cancel` (operate scope): a still-queued run is cancelled outright, 200 `{status: "cancelled"}`; a run already RUNNING is cancellable too, but asynchronously, so its owning node is stamped with a durable cancel request and the endpoint returns 202 Accepted with `{status: "cancelling"}` (poll `GET /api/v1/runs/{runId}` for the terminal outcome). An unknown id is 404 "No run '{runId}'."; an already-terminal run is 409 "Run '{runId}' has already finished and cannot be cancelled." The companion `POST /api/v1/runs/groups/{groupId}/cancel` cancels a whole run group the same way (queued members cancelled outright, running members sent a request): 202 `{status: "cancelling"}` when any member was still running, 200 `{status: "cancelled"}` otherwise, and 404 for an unknown group.

## Lineage read API

src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs serves the catalog's lineage graph:

- `GET /lineage/objects`: paged object list with `name` (contains), `serverRef`, and `kind` filters, ordered by `level` (the object's depth in the estate-wide data-movement graph, sources first; null level last), then database/schema/name. `ObjectDto` deliberately omits the heavy `Definition` field.
- `GET /lineage/objects/detail?key=...`: one object with its full module body; `definition` is null for plain tables, an unconnected sync, or an encrypted module.
- `GET /lineage/objects/columns?key=...`: paged columns of one object, ordered by ordinal.
- `GET /repos/{repoId}/lineage/edges`: paged attributed edges with `pipelineId`, `objectKey`, `relation`, and `tier` filters.
- `GET /repos/{repoId}/waves`: the execution plan grouped by wave, whole (not paged); wave `-1` means lineage has not been computed for those pipelines yet.
- `GET /repos/{repoId}/dependencies`: paged flow-to-flow dependencies.
- `GET /pipelines/{id}/columns` (src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs): the declared and detected pre-ingestion transform columns, declared-first.

Every query is read-only (`AsNoTracking`), projected to DTOs (raw EF entities never leave the host), and listing endpoints are bounded by page size. Repo-scoped endpoints verify the repo exists and return 404 for an unknown repo rather than an empty result a caller could not distinguish from a repo with no lineage yet.

## Configuration touchpoints

- Configuration section: `ControlPlane` (env form `ControlPlane__...`), documented in the table above; src/SqlFlow.ControlPlane/appsettings.json carries the non-secret defaults.
- Environment variables: `SQLFLOW_CATALOG_DB` (the default catalog connection reference), `SQLFLOW_GIT_TOKEN` and `SQLFLOW_GIT_USERNAME` (managed-sync and worker git credentials), `SQLFLOW_URL` and `SQLFLOW_TOKEN` (a standalone worker's control plane and node token).
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
