# SQLFlow Architecture

> This document summarizes design principles and has not been re-verified against the current
> codebase in full detail. For the current, code-verified behavior see
> [docs/reference/concepts/architecture-and-execution.md](reference/concepts/architecture-and-execution.md).

This document summarizes the design principles guiding the v3 rebuild.

## Goals

Modernize SQLFlow into a framework that is workable, portable, open-source friendly, and
adoptable - without losing the firepower that makes it valuable: **metadata-driven dynamic ETL
code generation for schema-evolving incremental loads against SQL Server.**

## Core principles

1. **SQL Server is the target, by design.** Deep integration is intentional; we do not pursue
   multi-engine portability. Language is C#/.NET.
2. **The generator is the heart, and it is stateful.** Generation is a pure function of
   `(definition, prior-state, live-schema)`. With prior state it emits *optimal incremental* code;
   without it, full / externally-bounded loads.
3. **The database is the source of truth (full mode).** The metadata model - definitions and
   runtime state - is authoritative. YAML is the authoring interface (git-first *workflow* over a
   DB-authoritative store), not the master.
4. **Two modes, one engine.** *Lightweight* (no control DB; runs from YAML; no memory/log) and
   *Full* (DB-backed; memory, logging, lineage, scheduling). The only difference is which providers
   are plugged in. **Memory is the upgrade, not the entry fee.**
5. **Separation of concerns.** The stateless engine is the core; memory, logging, lineage,
   scheduling, and the control plane are optional layers *on top of it*, never baked in.
6. **Control plane vs compute.** The control plane decides what/when and owns state; compute workers
   introspect, generate, and execute *near the data*. Workers pull work (no inbound connectivity),
   so they can run inside a customer's network and the data never leaves it.
7. **Secrets never live in YAML.** Credentials are referenced (`${env:…}`), not embedded;
   identity-based auth preferred; local-first defaults, cloud opt-in.
8. **Logic in code, not the database.** Generation/orchestration logic lives in the C# engine; the
   DB is a clean model + state store.

## Pipeline

```
YAML ──▶ [ model ] ──▶ infer source schema ──▶ introspect target schema
                                                       │
                                                       ▼
                                   diff ──▶ generate DDL ──▶ execute ──▶ bulk load
                                                       │
                            (full mode: read/write memory - watermark, run log)
```

## Source-of-truth & sync model (full mode, roadmap)

- DB is authoritative; YAML edits become real only via `apply`.
- Direction is chosen by the operation: `apply` (YAML→DB), `export`/`sync` (DB→YAML).
- DB wins on drift; optimistic concurrency (version each YAML derives from) keeps it non-destructive.
- This is **not** pure GitOps: git is a change-proposal + history channel; truth-history lives in
  the DB audit.

## Current status (this repo)

Implemented: the **stateless lightweight engine** end-to-end for the CSV → SQL Server slice with
schema evolution; the **control/compute split** (SqlFlow.ControlPlane + SqlFlow.Node with the durable
run queue, scheduler, node registry, and managed git sync); **identity** (regular SQLFlow users with
catalog-backed credentials, Microsoft Entra ID single sign-on via token exchange with JIT
provisioning, role/scope authorization, and first-run bootstrap provisioning of migrations, roles,
the initial admin, and an optional demo repo source); and the **GUI** (`gui/`): a React SPA over the
control plane API with dashboard, runs, fleet, pipelines (read-only Monaco YAML), schedules, repo
sources, lineage explorer/graph, search, and user administration, verified by a Playwright end-to-end
suite that boots the whole stack. Roadmap: additional source/target providers and a Monaco-based YAML
editor (authoring, not just viewing).

## Project structure

| Project | Responsibility | Notable dependencies |
|---|---|---|
| `SqlFlow.Core` | Model, abstractions, engine | none (pure) |
| `SqlFlow.SqlServer` | Introspection, DDL gen, bulk load, type mapping | Microsoft.Data.SqlClient |
| `SqlFlow.Sources` | Source readers + inference | CsvHelper |
| `SqlFlow.Yaml` | YAML ↔ model mapping/validation | YamlDotNet |
| `SqlFlow.Cli` | Command-line tool, DI composition root | Microsoft.Extensions.* |
