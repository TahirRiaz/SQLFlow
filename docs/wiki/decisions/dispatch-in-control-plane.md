---
id: wiki-dispatch-in-control-plane
title: "The run queue lives in the control plane, and a node speaks only to it"
type: decision
summary: "Why the run queue moved out of SQL Server into an in-memory dispatcher inside the control plane, what was rejected (portable SQL, a broker, per-replica queues, no journal), and the three decisions that changed while it shipped."
keywords:
  - dispatcher
  - run queue
  - node protocol
  - lease
  - fence
  - journal
  - ownership lease
  - scale target
  - KEDA
  - portability
sourceRefs:
  - src/SqlFlow.Dispatch/Dispatcher.cs
  - src/SqlFlow.Dispatch/DispatchState.cs
  - src/SqlFlow.Dispatch/IDispatchLedger.cs
  - src/SqlFlow.Dispatch/Protocol/NodeProtocol.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.Catalog/DispatchLeaseStore.cs
  - src/SqlFlow.Catalog/RunContextStore.cs
  - src/SqlFlow.Catalog/RunTraceStore.cs
  - src/SqlFlow.Catalog/ScaleTargetStore.cs
  - src/SqlFlow.Node/RunWorker.cs
  - src/SqlFlow.Node/NodeTraceFeed.cs
  - src/SqlFlow.ControlPlane/Dispatch/NodeProtocolEndpoints.cs
  - src/SqlFlow.ControlPlane/Dispatch/DispatchService.cs
rawRefs:
  - docs/dispatch-design.md
referenceRefs:
  - concept-control-plane
  - cli-worker
  - guide-deployment
related:
  - wiki-design-doc-drift
updated: 2026-09-11
---

# The run queue lives in the control plane, and a node speaks only to it

The run queue is an in-memory structure inside the control plane process. Every placement decision
(which node executes which run, in what order, under which gates) is made there under one lock, and
compute nodes pull work from it over an HTTP node protocol. The shadow catalog is the queue's
journal, written through with plain conditional updates and read back at start-up and by a periodic
reconcile; it is never the arbiter. A node holds a lease on what it executes and needs nothing but a
control-plane URL and a token: the run's definition arrives with the hand-out, the snapshotted YAML,
the lineage facts and the live trace travel over the same protocol, and the node never opens a
catalog connection.

For what the surface does, see [the dispatcher](../../reference/concepts/control-plane.md) and
[sqlflow worker](../../reference/cli/worker.md); for the manifests, the
[deployment guide](../../reference/guides/deployment.md). This page records why.

## What it replaced

Until 2026-09-11 the catalog's `Run` table was the queue. A node claimed with one T-SQL statement
under `UPDLOCK, READPAST, ROWLOCK`, a filtered unique index enforced one running execution per
pipeline (the claim's own predicate was only advisory under read committed, so the loser's duplicate
key error was caught and retried), a liveness reaper swept for silent nodes on a schedule, a running
run learned of a cancel by polling a stamped column, and the KEDA scaler evaluated a copy of the
claim's gating predicates in SQL with a second, Go-driver-shaped connection string. Every one of those
pieces was SQL Server specific, every compute node needed a catalog credential, and the pieces had to
be kept in step by hand. The owner's verdict was that this reduced portability to the point of making
the product unusable outside one database engine.

## Why in-process memory, journaled to the catalog

The gates are the queue. Pool routing, wave order inside a run group, the group's exact concurrency
cap and one execution per pipeline are global constraints over the whole active set, and they are
cheap to evaluate in memory under one lock and expensive to express portably in SQL. Holding the
state in the control plane makes every decision exact (the group cap is a cap, not "about this
many") and makes a long-polled hand-out possible, so a run on a standalone worker starts within
milliseconds of being queued rather than after a poll interval.

Memory alone would lose every scheduled fire that had not been handed out when the control plane
restarted, and the GUI would lose its queued view. So the catalog stays as a journal: an enqueue
writes the row before memory learns of it, a hand-out is reserved in memory, journaled, then
confirmed, and an outcome is journaled under the fence and then dropped from memory. That ordering
means the journal is never behind what a node holds, without a distributed transaction. Anything
that bypasses the in-process notify (a break-glass cancel written straight to the catalog, an
enqueue on a passive replica) is picked up by reconcile, which acts on additions at once and on
removals only when they persist across two passes, because the ledger read and memory move
independently.

Exactly one replica dispatches at a time, arbitrated by a lease row acquired and renewed with one
conditional update. A passive replica journals enqueues and answers reads; its node routes answer
503 with a retry hint. The alternative, a queue per replica with sticky nodes, cannot enforce
global gates and was rejected on that ground alone.

## Why leases and a fence instead of a reaper and a name

A hand-out is a lease renewed by every poll that reports the run held. A node that stops polling
stops renewing, and a lapsed lease is dispositioned exactly as a dead node's runs always were:
requeued while attempts remain, recorded cancelled when an operator's cancel was pending, failed
once the attempt budget is exhausted so a poison run cannot crash-loop the fleet. That removed both
the reaper's sweep and the node's name-based start-up recovery.

The attempt counter is the fence. Every hand-out advances it, and every per-run call a node makes
(its context request, each trace batch, its outcome report) presents the node name and the attempt
the hand-out carried; a node that was only presumed dead and finishes late presents a stale pair
and is refused, so the successor execution's rows and result are authoritative. The check runs in
the dispatcher's memory first and in the journal again, which is what makes a direct rewrite of a
row by an operator safe rather than a race.

## Why not the alternatives

- **Portable SQL in the same shape.** `SKIP LOCKED` exists elsewhere under other names and the
  filtered-index trick differs per provider, so the syntax could have been fixed, but every node would
  still have needed a catalog connection and the gates would still have been advisory. It fixed the
  spelling, not the coupling.
- **An external broker.** A message broker cannot evaluate the pipeline and wave gates, so a
  dispatcher in front of it would still have been needed, and it would have added an infrastructure
  dependency to every deployment including a laptop and the compose stack.
- **Memory with no journal.** Rejected for the restart and GUI reasons above; the journal costs one
  plain insert per enqueue, which the API already performed.

## Three decisions changed while it shipped

The design document, [dispatch-design.md](../../dispatch-design.md), was written and implemented on
the same day, and its status line records what it says here:

1. **The break-glass `sqlflow runs cancel --db` path stayed.** The design deleted it; reconcile makes a
   direct catalog cancel safe, so removing it would have cost an operator a tool for nothing.
2. **The node registry lives in memory and is flushed on a cadence.** Per-heartbeat writes would have
   cost the catalog one update per node per poll; a flush every few seconds costs a fleet of hundreds
   a few dozen updates a minute.
3. **The lineage facts are resolved on a separate call, not at hand-out time.** The design put the
   downstream watermark table and the landing-reset verdict in the hand-out. Only the parsed document
   says whether a flow participates, and the control plane cannot parse every run's document (a
   pinned run without a snapshot is materialized from git on the node), so the node asks once it has
   parsed, and the resolution code moved unchanged from the node to the catalog project.

A fourth followed in the last phase: the scaler's replica target is answered from the journal on
every replica rather than from the owner's memory, so an autoscaler behind a load balancer never
depends on which replica it reached, and the target divides the eligible backlog by the slot count
the nodes themselves report instead of a deployment parameter that had to match a constant in the
node runtime by hand.

## What generalizes

- A durable store makes a poor queue when the queue's rules are global constraints; keep the rules
  where they can be evaluated exactly, and keep the store as a journal with a defined write order.
- Liveness is a lease the holder renews, not a heartbeat somebody else sweeps for; a fencing token
  on every write from the holder is what turns "presumed dead" into a safe state.
- Anything a node needs should reach it through the one protocol it already speaks. Every extra
  credential on the compute tier is a portability and security cost, and the last one to go (the
  scaler's) was the one nobody thought of as a node's.
