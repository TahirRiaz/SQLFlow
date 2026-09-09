---
id: cli-worker
title: "sqlflow worker: self-hosted compute node"
type: cli-command
summary: Runs this host as a compute node that drains the catalog's durable run queue with atomic claims, pool routing, and node-local credential resolution.
keywords:
  - worker
  - run queue
  - pools
  - polling
  - compute node
  - claiming
  - crash recovery
  - attempt budget
  - claim fence
  - busy heartbeat
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
  - src/SqlFlow.Node/GitMaterializer.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.ControlPlane/Background/RunDispatcher.cs
  - src/SqlFlow.ControlPlane/Background/RunExecutionWorker.cs
  - Dockerfile.worker
  - deploy/docker/worker-entrypoint.sh
  - deploy/compose/docker-compose.yml
  - deploy/k8s/worker-pool.yaml
---

# sqlflow worker

## Synopsis

```bash
sqlflow worker [--db <conn-ref>] [--poll-seconds N] [--pool a,b] [--drain-seconds N] [-v]
```

## Description

Runs this host as a self-hosted compute node. The worker drains the shadow catalog's durable run queue: it atomically claims queued runs, executes each one through the same `DocumentExecutor` a direct CLI run uses (a worker run is byte-for-byte the same engine execution), and records the outcome under the run id the trigger already returned. A node inside a private network can therefore run the flows the control plane queued without the control plane ever reaching the node; the connection direction is strictly outbound (SQL to the catalog database, plus git to repo remotes for pinned runs).

The queue is the catalog's `[catalog].[Run]` table itself, so queued and running work survives restarts and any number of workers can drain it concurrently: each claim is one atomic T-SQL statement, so two nodes never double-run a flow. Every credential (the catalog connection, source and target connections referenced by flows, git tokens) resolves from this node's own environment; nothing sensitive travels through the queue.

The control plane hosts the very same drain loop in-process (`RunExecutionWorker` in src/SqlFlow.ControlPlane/Background/RunExecutionWorker.cs), so standalone workers are only needed when compute must live somewhere else: closer to the data, inside a network boundary, or scaled out horizontally.

The command runs until it receives a stop signal (SIGINT from Ctrl+C, or the SIGTERM an orchestrator sends when it reclaims the replica). A stop means "stop claiming and finish what you already hold": the in-flight runs keep executing and record their own outcomes, for up to `--drain-seconds`. See "Shutdown and the drain" below, which is the difference between a routine scale-in costing nothing and it destroying a run's progress.

## Arguments

The worker verb takes no positional argument. `worker` is in the parser's no-file verb list (src/SqlFlow.Cli/Program.cs), so a bare `sqlflow worker` (with only the default `--db`) starts the drain loop directly. It does not require a second positional token and never prints usage or exits 1 for a missing one. `--pool` and `--poll-seconds` are both registered value-taking options (src/SqlFlow.Cli/Program.cs, `ValueTakingOptions`), so their values are consumed as option values, not positionals, and can be placed anywhere on the command line.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database connection, as a secret reference (`${env:NAME}`, `${keyvault:vault/secret}`), resolved through the secret resolver. A resolution failure prints `ERROR  <redacted message>` to stderr and exits 1. |
| `--poll-seconds N` | integer | `5` | Queue poll cadence in seconds, with a floor of 1: `0` is raised to 1, and a negative or non-numeric value falls back to the default of 5. |
| `--pool a,b` | comma-separated list | empty | The pools this node serves. The node always drains untargeted runs; with `--pool` it additionally drains runs routed to any of the listed pools. Entries are trimmed and empty entries are dropped. |
| `--drain-seconds N` | integer | `540` | How long a stopping node keeps executing the runs it already claimed before severing them, with a floor of 0. Keep it BELOW the orchestrator's termination grace period, or the platform's kill lands mid-drain and severs the work anyway. `0` restores the pre-drain behavior (sever at once). |
| `-v`, `--verbose` | flag | off | Sets the console minimum log level to Debug (default is Information). |

## Behavior

### Startup

1. The `--db` reference is resolved. On failure the process exits 1 before anything else happens.
2. The node's identity is its machine name (`Environment.MachineName`). It is stamped onto every run this node claims (`ClaimedByNode`) so work is attributable and recoverable.
3. The worker prints its banner and starts the drain loop:

   ```text
   SQLFlow worker 'ETL-NODE-01' draining the run queue (poll 5s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
   ```

   With pools the banner lists them instead: `pools: onprem, finance`.

4. Orphan recovery runs once, before the first drain: any run left in status `running` and claimed by this node name (an orphan from a previous incarnation that stopped mid-run) is dispositioned exactly as the control plane's liveness reaper would (see "Crash recovery" below): requeued for another execution in the common case, recorded `cancelled` when an operator cancel was already pending, and `failed` only when it has exhausted its attempt budget. Recovery is best-effort; a briefly unreachable catalog does not stop the worker from starting.

### The drain loop

Each iteration heartbeats the node into the catalog's fleet registry (machine name, assembly version, last-seen timestamp, and `BusyRuns`, the number of runs this node is executing right now, which is the autoscaler's busy signal; the heartbeat is best-effort and never interrupts draining), then claims and executes runs one at a time until the queue is empty for this node, then waits `--poll-seconds` and repeats. The standalone worker idles on a plain delay; the control plane's in-process host idles on `RunQueueSignal` (a one-slot `SemaphoreSlim` nudge) with a 2 second poll fallback, so a triggered run starts within milliseconds there.

### Claiming

The claim is one atomic T-SQL UPDATE; the leading SET pins the READ COMMITTED isolation level, which READPAST requires (a pooled connection can carry a leftover level from a prior transaction). From src/SqlFlow.Catalog/RunQueueStore.cs:

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
UPDATE [catalog].[Run]
SET [Status] = @running, [ClaimedByNode] = @node, [StartUtc] = @now, [Attempt] = [Attempt] + 1
OUTPUT inserted.[RunId], inserted.[Attempt]
WHERE [RunId] = (
    SELECT TOP (1) [RunId] FROM [catalog].[Run] WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE [Status] = @queued AND {POOL_PREDICATE}
    ORDER BY [EnqueuedUtc], [RunId]);
```

The claim increments `[Attempt]` and returns it alongside the run id. That value is the claim's fencing token: every outcome write this node makes for the run (complete, fail, cancel) is conditional on the row still being `running`, claimed by this node, at exactly this attempt. It doubles as the execution counter that bounds crash-recovery requeues (see "Crash recovery" below).

`UPDLOCK` takes the update lock up front, `READPAST` makes concurrent workers skip rows another worker has already locked (each claim gets a different run instead of blocking), and the statement's atomicity is what makes any number of workers safe. `{POOL_PREDICATE}` is `[TargetPool] IS NULL` for a worker with no pools, or `([TargetPool] IS NULL OR [TargetPool] IN (@pool0, ...))` for a pooled worker; pool names are always bound as parameters.

Run statuses are the strings `queued`, `running`, `succeeded`, `failed`, `cancelled` (src/SqlFlow.Catalog/CatalogEntities.cs). Run ids are time-ordered (`Guid.CreateVersion7()`) and minted at enqueue, so the control plane's run read API reflects a run from the moment it is queued. A run can only be cancelled while still `queued`; once a worker has claimed it, cancellation reports not-cancellable.

### Version pinning and flow file resolution

At enqueue time a run is pinned to a commit: an explicit `commitSha` is honored verbatim, and when omitted the run is pinned to the repo's last successfully synced commit (`LastSyncedSha` of the repo's managed-sync source), provided the repo has a remote URL to materialize from. Only a repo with no resolvable synced commit produces an unpinned run.

On the worker:

- A SHA-pinned run is materialized from the repo's remote by `GitMaterializer` into a per-user cache at `<temp>/sqlflow/node-cache/<first-16-hex-of-sha256(remoteUrl)>/<commitSha>`. A materialized commit is reused across runs; a partial or wrong-commit directory is rebuilt from scratch. Git credentials come from `SQLFLOW_GIT_TOKEN` (with optional `SQLFLOW_GIT_USERNAME`; unset defaults to the `x-access-token` convention) on this node. A freshly provisioned node therefore needs no pre-synced repo state.
- An unpinned run executes from the repo's locally synced root path on this node.

The flow file is the repo root combined with the pipeline's relative path from the catalog.

### Execution and recording

The claimed run executes through the shared engine with the claimed id stamped as the run id, so the artifact and the catalog row record under exactly the id the trigger returned. Per-run substitution parameters travel from the queue row into `DocumentExecutionOptions.Parameters` (`FullLoad`, `BackfillFrom`, `BackfillTo`, `FilePattern`); this is the one handoff point shared by every flow kind.

On completion the outcome is recorded from the run's `run.json` artifact (status, timings, row counts, error, plus drill-down detail), under the claim fence: the write applies only while the row still carries this node's claim at this attempt. If the artifact is missing, oversized, or corrupt, the run is still driven to `failed` with the reason so it never lingers in `running`. Driving a run to `failed` is a no-op if the run is already terminal, so a late failure never overwrites a recorded success. If the fence rejects the write (the run was requeued out from under a node presumed dead, and possibly re-claimed), the node logs a warning and drops its result: the successor execution's outcome is authoritative, and the flows' idempotent loads (keyed merges, content-addressed landing) make the double execution harmless.

### Failure handling

One run's failure never tears down the loop: a per-run catch drives the run to a terminal `failed` state (with a secret-redacted error) and the loop continues to the next claim. A poll or claim error (a transient database outage) is logged as `Run queue poll error: <redacted>` and retried on the next tick.

Failure messages written to the run row include, verbatim from src/SqlFlow.Node/RunWorker.cs and src/SqlFlow.Node/GitMaterializer.cs:

| Message | Cause |
| --- | --- |
| `the run is not attributed to a repository.` | The run row has no repo id. |
| `the run's repository or pipeline is no longer in the catalog.` | The repo or pipeline row was removed between enqueue and claim. |
| `run is pinned to commit '<sha>' but repository '<name>' has no remote URL to materialize from.` | A pinned run on a repo without a remote. |
| `repository '<name>' has no synced root path on this node.` | An unpinned run on a node with no local copy. |
| `the flow file for '<flow>' was not found on this node.` | The resolved flow path does not exist. |
| `could not materialize '<remote>' at '<sha>': <cause>` | The git clone or checkout failed. |

### Crash recovery, the attempt budget, and the claim fence

Losing a worker mid-run is recoverable, never terminal for the pipeline. Two sweeps repair `running` rows whose executing process is gone:

- **Same-node restart recovery** (this worker's startup, step 4 above) matches orphans by this node's name.
- **The control plane's liveness reaper** (`OrphanRunReaper`, sweeping on `ControlPlane:Reaper:PollSeconds`) matches any run whose claiming node has not heartbeated within `StaleAfterSeconds`; it covers pods that die and never return under the same name.

Both apply the same disposition, implemented in `RunQueueStore` (src/SqlFlow.Catalog/RunQueueStore.cs):

1. An orphan with a pending operator cancel is recorded `cancelled`: the cancel intent is authoritative, and a requeue would resurrect work the operator explicitly killed.
2. An orphan whose `Attempt` is under `MaxExecutionAttempts` (3) goes back to `queued` with the claim cleared, for any eligible worker to claim again. The claim consumed the attempt, so the budget decrements even when the execution was lost.
3. An orphan that has consumed the whole budget is `failed` (its group dependents skipped): a run that repeatedly dies with its node is treated as the cause, not the victim. This is the poison-run bound that stops a memory-exhausting flow from crash-looping the fleet forever.

Every recovery write is a conditional update guarded on the exact orphaned claim (still `running`, same node, same attempt), so a run its real node completes in the same instant is never overwritten, and concurrent reaper replicas are idempotent.

The fence closes the zombie race: a node that was only presumed dead (its heartbeats blocked, its process alive) may finish after its run was requeued and re-claimed. Its outcome writes present the old attempt and are dropped; the successor's writes present the current attempt and land. Requeue is safe because every flow's load is idempotent: keyed merges collapse re-runs, landing skips byte-identical files, and wave gates hold group dependents while the requeued member is `queued`.

### Shutdown and the drain

Both stop signals are intercepted, so the process is never killed abruptly:

- **SIGTERM**, which is what an autoscaler reclaiming this replica, a revision swap, or `docker stop` sends. This is the one that matters in production; the runtime's default handling of it terminates the process on the spot.
- **SIGINT**, the interactive Ctrl+C.

Both are registered through `PosixSignalRegistration` with `Cancel = true` (src/SqlFlow.Cli/Program.cs), which suppresses the default termination and hands control back to the worker. It then:

1. **Stops claiming.** The queue is left alone, so queued runs stay available to other nodes.
2. **Drains.** Runs already in flight keep executing under a cancellation token that is deliberately NOT linked to the stopping token, so they finish and record their own outcomes. The node keeps heartbeating for the whole drain, which is load-bearing: a silent node is declared dead by the liveness reaper after `StaleAfterSeconds` and its runs are requeued, so a draining node that stopped beating would have the very work it is finishing re-executed underneath it.
3. **Severs only on timeout.** If work is still in flight after `--drain-seconds`, it is cancelled and left `running` for recovery, with a warning naming the expired window. A node cannot drain forever, because the platform that asked it to stop will kill it regardless.
4. Prints `SQLFlow worker stopped.` and exits 0.

A **second** stop signal skips the drain: it falls through to the runtime's default termination, so a worker is never unkillable. The severed runs are then requeued exactly as a crash would leave them.

Why this matters: a severed run records no outcome at all, so it is recovered only by the reaper's requeue, which **consumes one of its three execution attempts** and repeats all of its work. In an autoscaled fleet where replicas are reclaimed routinely, three unlucky stops inside one run's life will fail a perfectly healthy run and report that the run itself was the cause. The drain is what stops routine scale-in from manufacturing those failures.

**Operational requirement:** the drain window is only real if the orchestrator waits for it. Set the platform's termination grace period ABOVE `--drain-seconds` (Container Apps `terminationGracePeriodSeconds`, Kubernetes `terminationGracePeriodSeconds`; both default to 30 seconds, which is far too short for a bulk copy). With the default 540 second drain, the estate uses 600. If the grace period is left at the default, the drain starts correctly and is then killed 30 seconds in, which rescues only the runs that were nearly finished.

## Environment variables

| Variable | Read by | Purpose |
| --- | --- | --- |
| `SQLFLOW_CATALOG_DB` | the default `--db` reference | The catalog connection string. |
| `SQLFLOW_GIT_TOKEN` | `GitMaterializer` | Token for private git remotes when materializing pinned commits (optional). |
| `SQLFLOW_GIT_USERNAME` | `GitMaterializer` | Username paired with the token (optional; defaults to `x-access-token`). |
| `SQLFLOW_WORKER_POOL` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--pool`; keeps pool config in the environment. |
| `SQLFLOW_WORKER_POLL_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--poll-seconds`. |

In addition, every `${env:...}` reference used by the flows themselves (source and target connection strings) must resolve on this node: credentials are resolved at the edge, per node.

## Container image

Dockerfile.worker packages the worker as a container whose entrypoint (deploy/docker/worker-entrypoint.sh) composes the `sqlflow worker` invocation from `SQLFLOW_WORKER_POOL` and `SQLFLOW_WORKER_POLL_SECONDS`; the catalog connection stays on the CLI default `${env:SQLFLOW_CATALOG_DB}`, so it never appears in `ps` output. Both variables are optional, so a bare `sqlflow worker` still drains untargeted runs on the default poll cadence. The container exposes no ports and needs only outbound SQL and git. deploy/compose/docker-compose.yml runs it as the `worker` service, and deploy/k8s/worker-pool.yaml scales it with KEDA (an mssql scaler whose target is queued runs plus busy nodes, so occupied workers are never scaled away mid-run; scale-to-zero once nothing is queued and no node is busy).

## Examples

Run an untargeted worker on a VM that can reach the catalog:

```bash
export SQLFLOW_CATALOG_DB='Server=sql01;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True'
sqlflow worker --poll-seconds 5
```

```text
SQLFlow worker 'ETL-NODE-01' draining the run queue (poll 5s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
```

Serve two pools with a faster poll and debug logging:

```bash
sqlflow worker --pool onprem,finance --poll-seconds 2 -v
```

Point at a different catalog through an explicit reference:

```bash
sqlflow worker --db '${env:SQLFLOW_CATALOG_DB_PROD}' --poll-seconds 5
```

Run the containerized worker (built from the repository root):

```bash
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker run -d \
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
| The `--db` reference fails to resolve | 1 | `ERROR  <redacted message>` on stderr (two spaces after `ERROR`) |

Per-run failures do not affect the exit code; they are recorded on the run rows and logged, and the loop continues.

## See also

- [The control plane](../concepts/control-plane.md)
- [Architecture and execution](../concepts/architecture-and-execution.md)
- [Deployment](../guides/deployment.md)
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
