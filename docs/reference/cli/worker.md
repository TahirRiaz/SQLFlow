---
id: cli-worker
title: "sqlflow worker: self-hosted compute node"
type: cli-command
summary: Runs this host as a compute node that polls the control plane's dispatcher for work over HTTP, executes handed-out runs through the shared engine, streams their trace, and reports outcomes under a lease fence, with no catalog connection of its own.
keywords:
  - worker
  - node protocol
  - dispatcher
  - long poll
  - lease
  - pools
  - compute node
  - node token
  - execution spec
  - flow version
  - trace batches
  - attempt budget
  - revoked lease
  - graceful shutdown
  - drain
  - SIGTERM
  - termination grace period
  - scale-in
cliCommand: worker
related:
  - concept-control-plane
  - concept-architecture-and-execution
  - guide-deployment
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Node/RunWorker.cs
  - src/SqlFlow.Node/NodeTraceFeed.cs
  - src/SqlFlow.Node/HttpNodeTransport.cs
  - src/SqlFlow.Node/InProcessNodeTransport.cs
  - src/SqlFlow.Node/GitMaterializer.cs
  - src/SqlFlow.Dispatch/Protocol/NodeProtocol.cs
  - src/SqlFlow.Dispatch/Dispatcher.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/RunContextStore.cs
  - src/SqlFlow.Catalog/RunTraceStore.cs
  - src/SqlFlow.ControlPlane/Dispatch/NodeProtocolEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/RunExecutionWorker.cs
  - Dockerfile.worker
  - deploy/docker/worker-entrypoint.sh
  - deploy/compose/docker-compose.yml
  - deploy/k8s/worker-pool.yaml
---

# sqlflow worker

## Synopsis

```bash
sqlflow worker --url <control-plane> [--token <ref>] [--pool a,b] [--poll-seconds N] [--drain-seconds N] [-v]
```

## Description

Runs this host as a self-hosted compute node. The worker polls the control plane's dispatcher for work over HTTP (the node protocol under `/api/v1/node`, src/SqlFlow.Dispatch/Protocol/NodeProtocol.cs), executes each handed-out run through the same `DocumentExecutor` a direct CLI run uses (a worker run is byte-for-byte the same engine execution), streams the run's trace back, and reports the outcome under the run id the trigger already returned. Every call is outbound from the node, so a node inside a private network runs the flows the control plane queued without the control plane ever reaching it.

The queue itself lives in the control plane's memory, not in a database: the dispatcher decides who executes what (pool routing, wave order inside a run group, the group concurrency cap, one execution per pipeline) and journals each decision to the catalog with plain conditional updates (src/SqlFlow.Dispatch/Dispatcher.cs, src/SqlFlow.Catalog/RunQueueStore.cs). A node never claims anything; it is handed work and holds a lease on it.

A node needs no catalog connection. Everything a run needs travels over the node protocol: the hand-out carries the run's execution spec (repo, pipeline path, version pin, parameters), the snapshotted YAML is fetched by its content hash, the lineage facts an incremental or chained landing flow depends on are resolved by the control plane on request, and the generated SQL and canonical events stream back in batches. Every credential (the control plane token, source and target connections referenced by flows, git tokens) resolves from this node's own environment; nothing sensitive travels through the protocol, and the catalog connection never reaches a node at all.

The control plane hosts the very same loop in-process (`RunExecutionWorker` in src/SqlFlow.ControlPlane/Background/RunExecutionWorker.cs, over `InProcessNodeTransport`, which calls the dispatcher directly), so standalone workers are only needed when compute must live somewhere else: closer to the data, inside a network boundary, or scaled out horizontally.

The command runs until it receives a stop signal (SIGINT from Ctrl+C, or the SIGTERM an orchestrator sends when it reclaims the replica). A stop means "stop taking work and finish what you already hold": the in-flight runs keep executing and report their own outcomes, for up to `--drain-seconds`. See "Shutdown and the drain" below.

## Arguments

The worker verb takes no positional argument. `worker` is in the parser's no-file verb list (src/SqlFlow.Cli/Program.cs), so a bare `sqlflow worker` (with `SQLFLOW_URL` and `SQLFLOW_TOKEN` in the environment) starts the loop directly. `--url`, `--token`, `--pool`, `--poll-seconds` and `--drain-seconds` are value-taking options and can be placed anywhere on the command line.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--url <control-plane>` | absolute http(s) URL | `SQLFLOW_URL` | The control plane base URL. A missing or non-absolute URL prints `ERROR  no control plane is configured: ...` (or the malformed-URL message) to stderr and exits 1. |
| `--token <ref>` | secret reference or literal | `SQLFLOW_TOKEN`, then the credential `sqlflow login` stored for the URL | A personal access token minted with the `node` scope. A `${env:NAME}` / `${keyvault:vault/secret}` reference is resolved here on the node. None configured prints `ERROR  no node credential: ...` and exits 1. |
| `--pool a,b` | comma-separated list | empty | The pools this node serves. The node always takes untargeted runs; with `--pool` it additionally takes runs routed to any of the listed pools. Entries are trimmed and empty entries are dropped. |
| `--poll-seconds N` | integer | `30` | How long each poll asks the dispatcher to hold it when nothing is available, clamped to 1..60 (the protocol's maximum wait). Also the node's heartbeat cadence while it is saturated. |
| `--drain-seconds N` | integer | `540` | How long a stopping node keeps executing the runs it already holds before severing them, with a floor of 0. Keep it BELOW the orchestrator's termination grace period, or the platform's kill lands mid-drain and severs the work anyway. `0` severs at once. |
| `-v`, `--verbose` | flag | off | Sets the console minimum log level to Debug (default is Information). |

## Behavior

### Startup

1. The control plane URL and the node token are resolved. On any failure the process exits 1 before anything else happens.
2. The node's identity is its machine name (`Environment.MachineName`). It is stamped onto every run this node is handed (`ClaimedByNode`) so work is attributable and recoverable, and sent as the `X-SqlFlow-Node` header on every call so the control plane rate-limits per node rather than per shared token.
3. The worker prints its banner and starts polling:

   ```text
   SQLFlow worker 'ETL-NODE-01' polling https://sqlflow.example.com/ for work (poll 30s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
   ```

   With pools the banner lists them instead: `pools: onprem, finance`.

There is no startup recovery step: a run this node was executing when a previous incarnation died is dispositioned by the dispatcher when its lease lapses (see "Leases" below).

### The poll

One call, `POST /api/v1/node/poll`, is at once the heartbeat, the lease renewal, the cancel channel and the hand-out (src/SqlFlow.Node/HttpNodeTransport.cs). The node reports its name, build, pools, its capacity and free slots for runs and compute tasks, every run and task it currently holds (each run with the attempt its hand-out carried), when this incarnation started, and how long it is willing to wait. The dispatcher answers with:

- `runs` and `tasks`: new work, never more than the free slots the poll reported. Each run comes with the attempt the hand-out consumed, which is the fencing token the node presents on every later call for the run, and its execution spec (see "Version pinning and flow file resolution"). Each task comes with its operation, its connection reference and its payload.
- `cancelRuns` and `cancelTasks`: held work an operator has asked to cancel. The node aborts the in-flight statement and reports the run `cancelled`.
- `revokedRuns` and `revokedTasks`: held work whose lease the dispatcher no longer attributes to this node (it lapsed and the run was requeued). The node aborts it at once and reports nothing, because another node may already be executing it and the fence would drop the report anyway.
- `restartRequested`: an operator asked this node to restart (newer than this incarnation's start). The node stops taking work, drains, and exits so the orchestrator recreates it.
- `leaseSeconds`: how long the granted leases last unless renewed.

When the node has free slots and nothing is eligible, the dispatcher parks the call for up to `--poll-seconds` and answers the instant a run it could take is enqueued or becomes eligible, so a run handed to a standalone worker starts within milliseconds of being queued, not after a poll interval. When the node has no free slots the call is a heartbeat: it parks too, but only a signal for this node (a cancel, a revoked lease, a restart request) wakes it early. A slot freeing on the node cancels a parked heartbeat so the node re-polls with its new capacity at once.

A poll that cannot reach the control plane, or reaches a replica whose dispatcher is not the owner (HTTP 503 with a retry hint), is logged as `Dispatcher poll error: <redacted> (retrying in about Ns)` and retried with jittered backoff from 1 to 20 seconds. Held leases outlive several failed polls, so a brief control-plane blip never loses work.

### Leases

Every hand-out is a lease (default 90 seconds, `ControlPlane:Dispatch:LeaseSeconds`) renewed by every poll that reports the run held. A node that stops polling (a crash, an eviction, a network partition) stops renewing, and when the lease lapses the dispatcher dispositions the run exactly as a dead node's runs were always dispositioned (src/SqlFlow.Dispatch/Dispatcher.cs):

1. A run with a pending operator cancel is recorded `cancelled`: the cancel intent is authoritative, and a requeue would resurrect work the operator explicitly killed.
2. A run whose `Attempt` is under `MaxExecutionAttempts` (3) goes back to the queue with the holder cleared, for any eligible node to be handed again. The hand-out consumed the attempt, so the budget decrements even when the execution was lost. The interrupted attempt's live trace rows are discarded with it, so the run's trace is always the execution that produced its outcome.
3. A run that has consumed the whole budget is `failed` (its group dependents skipped): a run that repeatedly dies with its node is treated as the cause, not the victim. This is the poison-run bound that stops a memory-exhausting flow from crash-looping the fleet forever.

The fence closes the zombie race: a node that was only presumed dead (its polls blocked, its process alive) may finish after its run was requeued and handed out again. Every per-run call it makes presents the old attempt: its context request answers "not held", its trace batches are refused, and its outcome report is dropped as a stale claim; the successor's calls present the current attempt and land. Requeue is safe because every flow's load is idempotent: keyed merges collapse re-runs, landing skips byte-identical files, and wave gates hold group dependents while the requeued member is queued.

### Version pinning and flow file resolution

At enqueue time a run is pinned to a commit: an explicit `commitSha` is honored verbatim, and when omitted the run is pinned to the repo's last successfully synced commit (`LastSyncedSha` of the repo's managed-sync source), provided the repo has a remote URL to materialize from. Only a repo with no resolvable synced commit produces an unpinned run.

The hand-out's execution spec carries what the node needs to find the file: the repo's name, remote URL and root path, the pipeline's repo-relative path, the commit pin, the content hash of the snapshotted YAML, the run's substitution parameters, and the reference (never the value) of the git credential the repo's source names. The control plane reads it from the catalog before it journals the hand-out, so a read that fails leaves the run queued with no attempt consumed. On the worker:

- A run stamped with a flow-version hash executes from the snapshotted YAML, fetched once through `GET /api/v1/node/flow-versions/{hash}` and staged into a per-user cache at `<temp>/sqlflow/node-cache/yaml/<hash>`; the cache is content-addressed and consulted first, so a version this node has already staged costs no call at all, and no git access is needed.
- Otherwise a SHA-pinned run is materialized from the repo's remote by `GitMaterializer` into `<temp>/sqlflow/node-cache/<first-16-hex-of-sha256(remoteUrl)>/<commitSha>`. A materialized commit is reused across runs; a partial or wrong-commit directory is rebuilt from scratch. Git credentials come from the reference in the spec, resolved here, or from `SQLFLOW_GIT_TOKEN` (with optional `SQLFLOW_GIT_USERNAME`; unset defaults to the `x-access-token` convention) on this node.
- An unpinned run executes from the repo's locally synced root path on this node.

The flow file is the resolved root combined with the pipeline's relative path from the spec.

### Execution, context and the live trace

The handed-out run executes through the shared engine with its id stamped as the run id, so the artifact and the catalog row record under exactly the id the trigger returned. Per-run substitution parameters travel from the spec into `DocumentExecutionOptions.Parameters` (`FullLoad`, `BackfillFrom`, `BackfillTo`, `FilePattern`, `SourceFilter`, `AssertionsOnly`, `ReprocessFromSourceMin`); this is the one handoff point shared by every flow kind.

Once the document is parsed the node knows whether the flow participates in downstream-anchored watermarking (an incremental file or relational ingestion flow) or in the landing reset (an append-mode file flow with `load.resetWhenConsolidated` and a generated typed view). For those it asks the control plane once, `POST /api/v1/node/runs/{runId}/context`, naming the flow's own target; the control plane resolves the one unambiguous downstream table and the landing-reset verdict from the catalog's lineage graph and run ledger (src/SqlFlow.Catalog/RunContextStore.cs) and answers under the fence. An answer of "not held" means the lease lapsed meanwhile: the node aborts the execution and reports nothing. A control plane momentarily unreachable, or a replica that does not own dispatch, is retried with waits of 1, 2, 4, 8 and 15 seconds before the run is reported failed with the last error.

While the run executes, the node streams its generated SQL statements and canonical events (file progress, resolved watermarks, engine decisions, stage summaries, warnings) to `POST /api/v1/node/runs/{runId}/trace` in batches (src/SqlFlow.Node/NodeTraceFeed.cs): every 250 milliseconds, 200 items or 2 MB of text, whichever comes first, so the GUI's Statements and Events views update while the run is still running and the trace survives a mid-run crash. The feed never blocks the run: a retryable failure is retried with backoff for up to a minute, after which the feed stops and logs `live trace feed stopped after a batch could not be delivered`; a batch the dispatcher refuses (the lease lapsed) stops the feed with `the dispatcher no longer honors this node's lease`. The completion projection fills whatever a broken feed missed from the authoritative artifact.

On completion the node reads the run's `run.json` artifact and posts it to `POST /api/v1/node/runs/{runId}/outcome` with the attempt its hand-out carried; the control plane projects the result (status, timings, row counts, error, plus the drill-down detail the live feed did not already write) under the fence. An artifact over the protocol's 64 MB bound, or one the node cannot read, is reported as a failure with the reason, so the run never lingers `running`. A run that produced no artifact at all (the flow file was missing, the document failed to load, the worker threw) is reported `failed` with a secret-redacted error. If the fence rejects the report (`staleClaim`), the node logs a warning and drops its result: the successor execution's outcome is authoritative.

### Failure handling

One run's failure never tears down the loop: a per-run catch reports the run terminal and the loop continues. Failure messages reported on the run row include, verbatim from src/SqlFlow.Node/RunWorker.cs and src/SqlFlow.Node/GitMaterializer.cs:

| Message | Cause |
| --- | --- |
| `the run is not attributed to a repository.` | The run row has no repo id. |
| `the run's repository or pipeline is no longer in the catalog.` | The repo or pipeline row was removed between enqueue and hand-out, so the spec carries no name or path for it. |
| `run is pinned to commit '<sha>' but repository '<name>' has no remote URL to materialize from.` | A pinned run on a repo without a remote. |
| `repository '<name>' has no synced root path on this node.` | An unpinned run on a node with no local copy. |
| `the flow file for '<flow>' was not found on this node.` | The resolved flow path does not exist. |
| `could not materialize '<remote>' at '<sha>': <cause>` | The git clone or checkout failed. |
| `the run executed but its result could not be recorded: <reason>.` | The artifact was oversized or unreadable. |

A run whose row left the catalog between enqueue and hand-out is dropped by the dispatcher before any node sees it, so the node never reports that case.

### Shutdown and the drain

Both stop signals are intercepted, so the process is never killed abruptly:

- **SIGTERM**, which is what an autoscaler reclaiming this replica, a revision swap, or `docker stop` sends. This is the one that matters in production; the runtime's default handling of it terminates the process on the spot.
- **SIGINT**, the interactive Ctrl+C.

Both are registered through `PosixSignalRegistration` with `Cancel = true` (src/SqlFlow.Cli/Program.cs), which suppresses the default termination and hands control back to the worker. It then:

1. **Stops taking work.** Queued runs stay available to other nodes.
2. **Drains.** Runs already in flight keep executing under a cancellation token that is deliberately NOT linked to the stopping token, so they finish and report their own outcomes. The node keeps polling (with no free slots) for the whole drain, which is load-bearing: a silent node's leases lapse and its runs are requeued, so a draining node that stopped polling would have the very work it is finishing re-executed underneath it.
3. **Severs only on timeout.** If work is still in flight after `--drain-seconds`, it is cancelled and left for the dispatcher's lease expiry, with a warning naming the expired window. A severed run's trace feed drops whatever it still had queued rather than retrying it, so a node never hangs on the trace of a run it has abandoned. A node cannot drain forever, because the platform that asked it to stop will kill it regardless.
4. Prints `SQLFlow worker stopped.` and exits 0.

A **second** stop signal skips the drain: it falls through to the runtime's default termination, so a worker is never unkillable. The severed runs' leases lapse and the dispatcher requeues them exactly as a crash would.

Why this matters: a severed run reports no outcome at all, so it is recovered only by the lease expiry, which **consumes one of its three execution attempts** and repeats all of its work. In an autoscaled fleet where replicas are reclaimed routinely, three unlucky stops inside one run's life will fail a perfectly healthy run and report that the run itself was the cause. The drain is what stops routine scale-in from manufacturing those failures.

**Operational requirement:** the drain window is only real if the orchestrator waits for it. Set the platform's termination grace period ABOVE `--drain-seconds` (Container Apps `terminationGracePeriodSeconds`, Kubernetes `terminationGracePeriodSeconds`; both default to 30 seconds, which is far too short for a bulk copy). With the default 540 second drain, the estate uses 600.

## Environment variables

| Variable | Read by | Purpose |
| --- | --- | --- |
| `SQLFLOW_URL` | the default `--url` | The control plane base URL. |
| `SQLFLOW_TOKEN` | the default `--token` | The node's personal access token (`node` scope), or a `${env:...}`/`${keyvault:...}` reference to it. |
| `SQLFLOW_GIT_TOKEN` | `GitMaterializer` | Token for private git remotes when materializing pinned commits whose repo source names no credential reference (optional). |
| `SQLFLOW_GIT_USERNAME` | `GitMaterializer` | Username paired with the token (optional; defaults to `x-access-token`). |
| `SQLFLOW_WORKER_POOL` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--pool`; keeps pool config in the environment. |
| `SQLFLOW_WORKER_POLL_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--poll-seconds`. |
| `SQLFLOW_WORKER_DRAIN_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--drain-seconds`. |

`SQLFLOW_CATALOG_DB` is deliberately absent: a node never connects to the catalog. In addition, every `${env:...}` reference used by the flows themselves (source and target connection strings) must resolve on this node: credentials are resolved at the edge, per node.

## Minting a node token

The node authenticates with a personal access token whose scopes include `node`. The built-in `admin` role carries that scope, so an admin mints one through `POST /api/v1/me/tokens` with `{"name": "worker-pool-etl", "scopes": ["node"]}` (or the GUI's token page) and places the secret on the node as `SQLFLOW_TOKEN` or in the vault the worker's `${keyvault:...}` reference names. The break-glass bootstrap token endpoint (`POST /api/v1/auth/token`) also accepts the `node` scope for automation that provisions a fleet before any user exists. A token without the scope is answered 403 on every node route; no token is answered 401. One token may serve a whole fleet: the control plane keys its node rate limit on the token plus the node name each call carries, so hundreds of nodes sharing a token never starve one another.

## Container image

Dockerfile.worker packages the worker as a container whose entrypoint (deploy/docker/worker-entrypoint.sh) composes the `sqlflow worker` invocation from `SQLFLOW_WORKER_POOL`, `SQLFLOW_WORKER_POLL_SECONDS` and `SQLFLOW_WORKER_DRAIN_SECONDS`; the control plane URL and the node token stay on the CLI's own environment defaults, so neither appears in `ps` output. The container exposes no ports and needs only outbound HTTP to the control plane, SQL to the data its flows touch, and git for pinned runs without a snapshot. deploy/compose/docker-compose.yml runs it as the `worker` service, and deploy/k8s/worker-pool.yaml scales it with KEDA.

## Examples

Run an untargeted worker on a VM:

```bash
export SQLFLOW_URL='https://sqlflow.example.com'
export SQLFLOW_TOKEN='sqlf_...'
sqlflow worker
```

```text
SQLFlow worker 'ETL-NODE-01' polling https://sqlflow.example.com/ for work (poll 30s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
```

Serve two pools with a shorter poll and debug logging, resolving the token from a vault:

```bash
sqlflow worker --url https://sqlflow.example.com --token '${keyvault:sqlflow-v3-secrets/node-token}' --pool onprem,finance --poll-seconds 10 -v
```

Run the containerized worker (built from the repository root):

```bash
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker run -d \
  -e SQLFLOW_URL="https://sqlflow.example.com" \
  -e SQLFLOW_TOKEN="$NODE_TOKEN" \
  -e SQLFLOW_WORKER_POOL='onprem' \
  -e SQLFLOW_GIT_TOKEN="$GIT_TOKEN" \
  sqlflow-worker:latest
```

Scale compute in the compose stack with no other change:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --scale worker=3
```

## Exit behavior

| Condition | Exit code | Output |
| --- | --- | --- |
| Clean stop after a drain (Ctrl+C, SIGTERM, or an operator restart request) | 0 | `SQLFlow worker stopped.` |
| No control plane URL, no node credential, or a token reference that does not resolve | 1 | `ERROR  ...` on stderr before the banner |
| A second stop signal during the drain | the runtime's default termination | nothing further; the severed runs are requeued by the dispatcher |
