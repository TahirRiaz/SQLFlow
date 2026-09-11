---
id: wiki-design-doc-drift
title: "Design document drift map: which docs under docs/ can still be trusted"
type: map
summary: "Which documents under docs/ are historical design intent and which are current, the drift each declares, and how the api prose gap was closed."
keywords:
  - drift
  - design documents
  - historical
  - superseded
  - acquisition
  - api flow
  - documentation map
rawRefs:
  - docs/architecture.md
  - docs/acquisition.md
  - docs/adf-dropin.md
  - docs/connection-registry-design.md
  - docs/connection-registry-corrections.md
  - docs/environment-variables.md
  - docs/ingestion-engine-design.md
  - docs/pre-ingestion-transform-design.md
  - docs/schema-evolution-design.md
  - docs/schema-sync-and-discovery-design.md
  - docs/flattener-memory-postmortem.md
  - docs/dispatch-design.md
referenceRefs:
  - concept-architecture-and-execution
  - concept-control-plane
  - cli-worker
  - concept-connections-and-secrets
  - concept-environment-variables
  - concept-ingestion-run-pipeline
related:
  - wiki-string-first-landing
  - wiki-census-drift
updated: 2026-09-11
---

# Design document drift map: which docs under docs/ can still be trusted

Twelve markdown documents sit directly under `docs/`, above the verified `docs/reference/` corpus.
They are not equivalent to each other. Eight are pre-implementation design intent that the shipped
code has moved away from, and each declares its own drift in a banner. Two are current prose. One is
an incident record. One is a phased design whose status line tracks which phases have shipped.

The practical rule: **for behavior, go to `docs/reference/`. Come here only for intent and
history.** A design document tells you what someone meant to build, which is genuinely useful when
you are trying to understand why a subsystem is shaped the way it is, and actively misleading if you
read it as a description of what runs today.

## Historical design intent (superseded, banner present)

Each of these opens with a banner naming its own drift and pointing at the reference pages that
replace it. Trust the banner; it was written against the code.

| Document | Status it declares | Superseded by |
| --- | --- | --- |
| [architecture.md](../../architecture.md) | Design principles, not re-verified in full detail | `concepts/architecture-and-execution.md` |
| [adf-dropin.md](../../adf-dropin.md) | Not re-verified | `flow/inv.md`, `concepts/control-plane.md` |
| [connection-registry-design.md](../../connection-registry-design.md) | Pre-implementation; enumerates four specific divergences (no `Synapse` `DataSourceKind`, connections live in `SqlFlow.Core/Connections/`, `ISecretResolver` is async, `IConnectionFactory` returns `DbConnection`) | `concepts/connections-and-secrets.md`, `flow/connections.md` |
| [connection-registry-corrections.md](../../connection-registry-corrections.md) | A red-team critique of the above, not shipped behavior; the as-built model diverged from **both** documents | `concepts/connections-and-secrets.md`, `flow/connections.md` |
| [environment-variables.md](../../environment-variables.md) | Predates the reference corpus; omits the `SQLFLOW_AZURE_AUTH` aliases (`sp`, `mi`, `msi`, `azurecli`, `azlogin`) and the `detect-unique-key` `--source` default | `concepts/environment-variables.md` |
| [ingestion-engine-design.md](../../ingestion-engine-design.md) | Reasons from the legacy `flw.Ingestion` schema; not re-verified | `flow/ing.md` and companions, `concepts/ingestion-run-pipeline.md` |
| [pre-ingestion-transform-design.md](../../pre-ingestion-transform-design.md) | Pre-implementation; not re-verified | `concepts/pre-ingestion-transform.md`, `flow/transform.md` |
| [schema-evolution-design.md](../../schema-evolution-design.md) | Pre-implementation legacy study plus an early slice | `concepts/schema-evolution.md`, `flow/schema.md` |
| [schema-sync-and-discovery-design.md](../../schema-sync-and-discovery-design.md) | The shipped CLI surface diverged: it specifies `sqlflow discover <databases\|schemas\|...>` with `--no-cache`, but the shipped verbs are `sqlflow catalog <...>`, `sqlflow discover` is the JSON/XML introspection verb, and `--no-cache` does not exist | `cli/catalog.md`, `cli/discover.md` |

## Unbannered, and current

[acquisition.md](../../acquisition.md) describes `flowType: api`, the declarative acquisition engine
that replaced the estate's Azure Automation runbooks. It carries no drift banner.

Until 2026-09-09 it was also the **only** prose describing that flow kind: `docs/reference/flow/` had
a page for every other kind (`ing`, `exp`, `sp`, `inv`, `hc`, `scm`, `cpy`, `cal`, `trl`, `batch`,
`sftp`) but for `api` there was only the machine-readable key census `keys.api.json`, which the LSP
and VSCode extension consume and which carries no narrative. Three reference pages (`cpy`, `sftp`,
`trl`) already listed `flow-api` in their `related`, and the manifest builder was pruning it as a
dangling id on every run.

That gap is now closed by [reference/flow/api.md](../../reference/flow/api.md), which is verified
against the engine rather than against the census, and therefore also documents the nine key paths
the census is missing (see [census-drift](census-drift.md)). `acquisition.md` remains useful as the
design narrative for why the acquisition engine exists at all, and is no longer load-bearing for
behaviour.

## A phased design, tracked by its own status line

[dispatch-design.md](../../dispatch-design.md) is the design for moving the run queue out of SQL
Server into an in-memory dispatcher inside the control plane, with compute nodes pulling work over an
HTTP node protocol. Unlike the historical documents above it was written on 2026-09-11 and
implemented the same day: its status line records that phases 1 and 2 shipped (the queue, the node
protocol, the ownership lease, the tests; then the execution spec in every hand-out and the
flow-version, context and trace calls that let a node run with no catalog connection) and that
phase 3 remains, and it names the three decisions that changed during implementation. Its sections 2
and 3 deliberately describe the state it replaced, so "today" there means the SQL-claim design, not
what runs now; for the shipped behaviour go to
[reference/concepts/control-plane.md](../../reference/concepts/control-plane.md) ("The dispatcher")
and [reference/cli/worker.md](../../reference/cli/worker.md). Until phase 3 lands, the status line
is the authoritative statement of what is implemented.

## Incident record

[flattener-memory-postmortem.md](../../flattener-memory-postmortem.md) is a postmortem, not a design
document: an `OutOfMemoryException` in `XmlPathFlattener` and `JsonPathFlattener`, dated
2026-08-20 / 2026-08-21. It describes a specific failure and its fix rather than intended behavior,
so it does not drift in the same way. It has not been ingested into this wiki; its generalizable
lesson belongs in `incidents/`.
