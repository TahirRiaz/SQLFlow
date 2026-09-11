---
id: cli-worker
title: "sqlflow worker: self-hosted compute node"
type: cli-command
summary: Runs this host as a compute node that polls the control plane's dispatcher for work over HTTP, executes handed-out runs through the shared engine, and reports outcomes under a lease fence.
keywords:
  - worker
  - node protocol
  - dispatcher
  - long poll
  - lease
  - pools
  - compute node
  - node token
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
  - src/SqlFlow.Node/HttpNodeTransport.cs
  - src/SqlFlow.Node/InProcessNodeTransport.cs
  - src/SqlFlow.Node/GitMaterializer.cs
  - src/SqlFlow.Dispatch/Protocol/NodeProtocol.cs
  - src/SqlFlow.Dispatch/Dispatcher.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
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
sqlflow worker --url <control-plane> [--token <ref>] [--db <conn-ref>] [--pool a,b] [--poll-seconds N] [--drain-seconds N] [-v]
```

## Description

Runs this host as a self-hosted compute node. The worker polls the control plane's dispatcher for work over HTTP (the node protocol under `/api/v1/node`, src/SqlFlow.Dispatch/Protocol/NodeProtocol.cs), executes each handed-out run through the same `DocumentExecutor` a direct CLI run uses (a worker run is byte-for-byte the same engine execution), and reports the outcome under the run id the trigger already returned. Every call is outbound from the node, so a node inside a private network runs the flows the control plane queued without the control plane ever reaching it.

The queue itself lives in the control plane's memory, not in a database: the dispatcher decides who executes what (pool routing, wave order inside a run group, the group concurrency cap, one execution per pipeline) and journals each decision to the catalog with plain conditional updates (src/SqlFlow.Dispatch/Dispatcher.cs, src/SqlFlow.Catalog/RunQueueStore.cs). A node never claims anything; it is handed work and holds a lease on it. Every credential (the control plane token, the catalog connection, source and target connections referenced by flows, git tokens) resolves from this node's own environment; nothing sensitive travels through the protocol.

The catalog connection is still required: the node reads the run's definition (repo, pipeline path, snapshotted YAML) and streams the run's statements and events into the catalog live. No queue operation touches it.

The control plane hosts the very same loop in-process (`RunExecutionWorker` in src/SqlFlow.ControlPlane/Background/RunExecutionWorker.cs, over `InProcessNodeTransport`, which calls the dispatcher directly), so standalone workers are only needed when compute must live somewhere else: closer to the data, inside a network boundary, or scaled out horizontally.

The command runs until it receives a stop signal (SIGINT from Ctrl+C, or the SIGTERM an orchestrator sends when it reclaims the replica). A stop means "stop taking work and finish what you already hold": the in-flight runs keep executing and report their own outcomes, for up to `--drain-seconds`. See "Shutdown and the drain" below.

## Arguments

The worker verb takes no positional argument. `worker` is in the parser's no-file verb list (src/SqlFlow.Cli/Program.cs), so a bare `sqlflow worker` (with `SQLFLOW_URL` and `SQLFLOW_TOKEN` in the environment) starts the loop directly. `--url`, `--token`, `--db`, `--pool`, `--poll-seconds` and `--drain-seconds` are value-taking options and can be placed anywhere on the command line.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--url <control-plane>` | absolute http(s) URL | `SQLFLOW_URL` | The control plane base URL. A missing or non-absolute URL prints `ERROR  no control plane is configured: ...` (or the malformed-URL message) to stderr and exits 1. |
| `--token <ref>` | secret reference or literal | `SQLFLOW_TOKEN`, then the credential `sqlflow login` stored for the URL | A personal access token minted with the `node` scope. A `${env:NAME}` / `${keyvault:vault/secret}` reference is resolved here on the node. None configured prints `ERROR  no node credential: ...` and exits 1. |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database connection, resolved through the secret resolver, for run definitions and trace streaming. A resolution failure prints `ERROR  <redacted message>` and exits 1. |
| `--pool a,b` | comma-separated list | empty | The pools this node serves. The node always takes untargeted runs; with `--pool` it additionally takes runs routed to any of the listed pools. Entries are trimmed and empty entries are dropped. |
| `--poll-seconds N` | integer | `30` | How long each poll asks the dispatcher to hold it when nothing is available, clamped to 1..60 (the protocol's maximum wait). Also the node's heartbeat cadence while it is saturated. |
| `--drain-seconds N` | integer | `540` | How long a stopping node keeps executing the runs it already holds before severing them, with a floor of 0. Keep it BELOW the orchestrator's termination grace period, or the platform's kill lands mid-drain and severs the work anyway. `0` severs at once. |
| `-v`, `--verbose` | flag | off | Sets the console minimum log level to Debug (default is Information). |

## Behavior

### Startup

1. The control plane URL, the node token and the `--db` reference are resolved. On any failure the process exits 1 before anything else happens.
2. The node's identity is its machine name (`Environment.MachineName`). It is stamped onto every run this node is handed (`ClaimedByNode`) so work is attributable and recoverable.
3. The worker prints its banner and starts polling:

   ```text
   SQLFlow worker 'ETL-NODE-01' polling https://sqlflow.example.com/ for work (poll 30s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
   ```

   With pools the banner lists them instead: `pools: onprem, finance`.

There is no startup recovery step: a run this node was executing when a previous incarnation died is dispositioned by the dispatcher when its lease lapses (see "Leases" below).

### The poll

One call, `POST /api/v1/node/poll`, is at once the heartbeat, the lease renewal, the cancel channel and the hand-out (src/SqlFlow.Node/HttpNodeTransport.cs). The node reports its name, build, pools, its capacity and free slots for runs and compute tasks, every run and task it currently holds (each run with the attempt its hand-out carried), when this incarnation started, and how long it is willing to wait. The dispatcher answers with:

- `runs` and `tasks`: new work, never more than the free slots the poll reported. Each run comes with the attempt the hand-out consumed, which is the fencing token the node presents on its outcome report.
- `cancelRuns` and `cancelTasks`: held work an operator has asked to cancel. The node aborts the in-flight statement and reports the run `cancelled`.
- `revokedRuns` and `revokedTasks`: held work whose lease the dispatcher no longer attributes to this node (it lapsed and the run was requeued). The node aborts it at once and reports nothing, because another node may already be executing it and the fence would drop the report anyway.
- `restartRequested`: an operator asked this node to restart (newer than this incarnation's start). The node stops taking work, drains, and exits so the orchestrator recreates it.
- `leaseSeconds`: how long the granted leases last unless renewed.

When the node has free slots and nothing is eligible, the dispatcher parks the call for up to `--poll-seconds` and answers the instant a run it could take is enqueued or becomes eligible, so a run handed to a standalone worker starts within milliseconds of being queued, not after a poll interval. When the node has no free slots the call is a heartbeat: it parks too, but only a signal for this node (a cancel, a revoked lease, a restart request) wakes it early. A slot freeing on the node cancels a parked heartbeat so the node re-polls with its new capacity at once.

A poll that cannot reach the control plane, or reaches a replica whose dispatcher is not the owner (HTTP 503 with a retry hint), is logged as `Dispatcher poll error: <redacted> (retrying in about Ns)` and retried with jittered backoff from 1 to 20 seconds. Held leases outlive several failed polls, so a brief control-plane blip never loses work.

### Leases

Every hand-out is a lease (default 90 seconds, `ControlPlane:Dispatch:LeaseSeconds`) renewed by every poll that reports the run held. A node that stops polling (a crash, an eviction, a network partition) stops renewing, and when the lease lapses the dispatcher dispositions the run exactly as a dead node's runs were always dispositioned (src/SqlFlow.Dispatch/Dispatcher.cs):

1. A run with a pending operator cancel is recorded `cancelled`: the cancel intent is authoritative, and a requeue would resurrect work the operator explicitly killed.
2. A run whose `Attempt` is under `MaxExecutionAttempts` (3) goes back to the queue with the holder cleared, for any eligible node to be handed again. The hand-out consumed the attempt, so the budget decrements even when the execution was lost.
3. A run that has consumed the whole budget is `failed` (its group dependents skipped): a run that repeatedly dies with its node is treated as the cause, not the victim. This is the poison-run bound that stops a memory-exhausting flow from crash-looping the fleet forever.

The fence closes the zombie race: a node that was only presumed dead (its polls blocked, its process alive) may finish after its run was requeued and handed out again. Its outcome report presents the old attempt and is dropped as a stale claim; the successor's report presents the current attempt and lands. Requeue is safe because every flow's load is idempotent: keyed merges collapse re-runs, landing skips byte-identical files, and wave gates hold group dependents while the requeued member is queued.

### Version pinning and flow file resolution

At enqueue time a run is pinned to a commit: an explicit `commitSha` is honored verbatim, and when omitted the run is pinned to the repo's last successfully synced commit (`LastSyncedSha` of the repo's managed-sync source), provided the repo has a remote URL to materialize from. Only a repo with no resolvable synced commit produces an unpinned run.

On the worker:

- A run stamped with a flow-version hash executes from the catalog's snapshotted YAML, staged into a per-user cache at `<temp>/sqlflow/node-cache/yaml/<hash>`; no git access is needed.
- Otherwise a SHA-pinned run is materialized from the repo's remote by `GitMaterializer` into `<temp>/sqlflow/node-cache/<first-16-hex-of-sha256(remoteUrl)>/<commitSha>`. A materialized commit is reused across runs; a partial or wrong-commit directory is rebuilt from scratch. Git credentials come from `SQLFLOW_GIT_TOKEN` (with optional `SQLFLOW_GIT_USERNAME`; unset defaults to the `x-access-token` convention) on this node.
- An unpinned run executes from the repo's locally synced root path on this node.

The flow file is the resolved root combined with the pipeline's relative path from the catalog.

### Execution and reporting

The handed-out run executes through the shared engine with its id stamped as the run id, so the artifact and the catalog row record under exactly the id the trigger returned. Per-run substitution parameters travel from the run row into `DocumentExecutionOptions.Parameters` (`FullLoad`, `BackfillFrom`, `BackfillTo`, `FilePattern`, `SourceFilter`, `AssertionsOnly`, `ReprocessFromSourceMin`); this is the one handoff point shared by every flow kind.

On completion the node reads the run's `run.json` artifact and posts it to `POST /api/v1/node/runs/{runId}/outcome` with the attempt its hand-out carried; the control plane projects the result (status, timings, row counts, error, plus the drill-down detail the live trace feed did not already write) under the fence. An artifact over the protocol's 64 MB bound, or one the node cannot read, is reported as a failure with the reason, so the run never lingers `running`. A run that produced no artifact at all (the flow file was missing, the document failed to load, the worker threw) is reported `failed` with a secret-redacted error. If the fence rejects the report (`staleClaim`), the node logs a warning and drops its result: the successor execution's outcome is authoritative.

### Failure handling

One run's failure never tears down the loop: a per-run catch reports the run terminal and the loop continues. Failure messages reported on the run row include, verbatim from src/SqlFlow.Node/RunWorker.cs and src/SqlFlow.Node/GitMaterializer.cs:

| Message | Cause |
| --- | --- |
| `the run is no longer in the catalog.` | The run row was removed between hand-out and load. |
| `the run is not attributed to a repository.` | The run row has no repo id. |
| `the run's repository or pipeline is no longer in the catalog.` | The repo or pipeline row was removed between enqueue and hand-out. |
| `run is pinned to commit '<sha>' but repository '<name>' has no remote URL to materialize from.` | A pinned run on a repo without a remote. |
| `repository '<name>' has no synced root path on this node.` | An unpinned run on a node with no local copy. |
| `the flow file for '<flow>' was not found on this node.` | The resolved flow path does not exist. |
| `could not materialize '<remote>' at '<sha>': <cause>` | The git clone or checkout failed. |
| `the run executed but its result could not be recorded: <reason>.` | The artifact was oversized or unreadable. |

### Shutdown and the drain

Both stop signals are intercepted, so the process is never killed abruptly:

- **SIGTERM**, which is what an autoscaler reclaiming this replica, a revision swap, or `docker stop` sends. This is the one that matters in production; the runtime's default handling of it terminates the process on the spot.
- **SIGINT**, the interactive Ctrl+C.

Both are registered through `PosixSignalRegistration` with `Cancel = true` (src/SqlFlow.Cli/Program.cs), which suppresses the default termination and hands control back to the worker. It then:

1. **Stops taking work.** Queued runs stay available to other nodes.
2. **Drains.** Runs already in flight keep executing under a cancellation token that is deliberately NOT linked to the stopping token, so they finish and report their own outcomes. The node keeps polling (with no free slots) for the whole drain, which is load-bearing: a silent node's leases lapse and its runs are requeued, so a draining node that stopped polling would have the very work it is finishing re-executed underneath it.
3. **Severs only on timeout.** If work is still in flight after `--drain-seconds`, it is cancelled and left for the dispatcher's lease expiry, with a warning naming the expired window. A node cannot drain forever, because the platform that asked it to stop will kill it regardless.
4. Prints `SQLFlow worker stopped.` and exits 0.

A **second** stop signal skips the drain: it falls through to the runtime's default termination, so a worker is never unkillable. The severed runs' leases lapse and the dispatcher requeues them exactly as a crash would.

Why this matters: a severed run reports no outcome at all, so it is recovered only by the lease expiry, which **consumes one of its three execution attempts** and repeats all of its work. In an autoscaled fleet where replicas are reclaimed routinely, three unlucky stops inside one run's life will fail a perfectly healthy run and report that the run itself was the cause. The drain is what stops routine scale-in from manufacturing those failures.

**Operational requirement:** the drain window is only real if the orchestrator waits for it. Set the platform's termination grace period ABOVE `--drain-seconds` (Container Apps `terminationGracePeriodSeconds`, Kubernetes `terminationGracePeriodSeconds`; both default to 30 seconds, which is far too short for a bulk copy). With the default 540 second drain, the estate uses 600.

## Environment variables

| Variable | Read by | Purpose |
| --- | --- | --- |
| `SQLFLOW_URL` | the default `--url` | The control plane base URL. |
| `SQLFLOW_TOKEN` | the default `--token` | The node's personal access token (`node` scope), or a `${env:...}`/`${keyvault:...}` reference to it. |
| `SQLFLOW_CATALOG_DB` | the default `--db` reference | The catalog connection string, for run definitions and trace streaming. |
| `SQLFLOW_GIT_TOKEN` | `GitMaterializer` | Token for private git remotes when materializing pinned commits (optional). |
| `SQLFLOW_GIT_USERNAME` | `GitMaterializer` | Username paired with the token (optional; defaults to `x-access-token`). |
| `SQLFLOW_WORKER_POOL` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--pool`; keeps pool config in the environment. |
| `SQLFLOW_WORKER_POLL_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--poll-seconds`. |
| `SQLFLOW_WORKER_DRAIN_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--drain-seconds`. |

In addition, every `${env:...}` reference used by the flows themselves (source and target connection strings) must resolve on this node: credentials are resolved at the edge, per node.

## Minting a node token

The node authenticates with a personal access token whose scopes include `node`. The built-in `admin` role carries that scope, so an admin mints one through `POST /api/v1/me/tokens` with `{"name": "worker-pool-etl", "scopes": ["node"]}` (or the GUI's token page) and places the secret on the node as `SQLFLOW_TOKEN` or in the vault the worker's `${keyvault:...}` reference names. The break-glass bootstrap token endpoint (`POST /api/v1/auth/token`) also accepts the `node` scope for automation that provisions a fleet before any user exists. A token without the scope is answered 403 on every node route; no token is answered 401.

## Container image

Dockerfile.worker packages the worker as a container whose entrypoint (deploy/docker/worker-entrypoint.sh) composes the `sqlflow worker` invocation from `SQLFLOW_WORKER_POOL`, `SQLFLOW_WORKER_POLL_SECONDS` and `SQLFLOW_WORKER_DRAIN_SECONDS`; the control plane URL, the node token and the catalog connection stay on the CLI's own environment defaults, so none of them appears in `ps` output. The container exposes no ports and needs only outbound HTTP to the control plane, SQL to the catalog and the data, and git for pinned runs without a snapshot. deploy/compose/docker-compose.yml runs it as the `worker` service, and deploy/k8s/worker-pool.yaml scales it with KEDA.

## Examples

Run an untargeted worker on a VM:

```bash
export SQLFLOW_URL='https://sqlflow.example.com'
export SQLFLOW_TOKEN='sqlf_...'
export SQLFLOW_CATALOG_DB='Server=sql01;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True'
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
  -e SQLFLOW_CATALOG_DB="Server=catalog;Database=SqlFlowCatalog;User ID=sqlflow;Password=$SQL_PASSWORD;TrustServerCertificate=True" \
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
| Clean stop via SIGINT (Ctrl+C) or SIGTERM, after the drain | 0 | `SQLFlow worker stopped.` on stdout |
| No control plane URL, no node token, or a reference that fails to resolve | 1 | `ERROR  <message>` on stderr (two spaces after `ERROR`) |

Per-run failures do not affect the exit code; they are reported on the run rows and logged, and the loop continues.

## See also

- [The control plane](../concepts/control-plane.md)
- [Architecture and execution](../concepts/architecture-and-execution.md)
- [Deployment](../guides/deployment.md)
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
