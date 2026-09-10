# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Document-envelope `mode:` on every flow kind, with a new `disabled` value. `mode: manual` was
  documented as excluding a flow from schedule fires and batch/node group runs, but only health
  checks actually parsed the key; now the envelope reads `auto | manual | disabled` for all kinds
  and projects it to the pipeline row. `disabled` deactivates a retired pipeline: excluded from all
  automatic execution and from descendant selection, exempt from the "attached to no schedule"
  sync warning, shown with a `disabled` badge, and still runnable by a direct trigger (a Node
  anchor is kept whatever its mode). Node-scoped runs select only active (`mode: auto`)
  descendants by default; a new `includeAll` ("find all") option on the trigger/preview API and a
  switch in the GUI run dialog widens a node run to its manual and disabled descendants for a
  deliberate replay. Fixes deactivated sources being pulled into "run with descendants" groups.
- `load.resetWhenConsolidated` on a file flow (default ON): a chained landing (bronze) table is reset,
  truncated at the start of the next run, once every flow that directly reads its typed view has
  completed a successful run after the landing's last successful load. One hop only: delivery to
  silver frees bronze; gold is irrelevant. The node proves delivery from the catalog's lineage graph
  and run ledger and the engine truncates only after the source read finds files, so a quiet day, a
  lagging/failed consumer, a base-table reader, a seeded table with no run history, a backfill
  bounded to a window or filter, or a direct CLI run all keep the rows, each with the blocking
  reason on the run's events. A plain forced full load keeps the gate and, when authorized, becomes
  a clean staging rebuild instead of doubling the table. Fixes landing tables growing without bound
  (the `pre` estate had accumulated 794M rows / 232 GB of already-consolidated staging data).
- The ing-side `load.truncateSourceWhenConsolidated` gate now scopes its target-side `MAX(watermark)`
  probe by `source.incrementalClause`, so on a shared target (several operators merging into one arc
  table) another operator's fresher load can no longer fake the catch-up and truncate un-consolidated
  landing rows.
- `incremental.lookback` on an `ing` flow: the numeric counterpart of `incremental.overlapDays`, subtracted
  from a non-date watermark's `MAX` (and, with `fetchMinValuesFromSource`, from the source `MIN`) inside the
  probe itself. A monotonic id is allocated at `INSERT` but published at `COMMIT`, so a bare `MAX` can
  advance past a row still in flight and the next run's strict `>` skips it permanently; a lookback re-reads
  that window and the keyed upsert dedupes it. Sized in key units, defaults to `0` (the previous bare `MAX`),
  and is skipped for a watermark type arithmetic does not apply to (string, binary, rowversion, float/real).
- `subscribers.<name>.notes`: free-form remarks about a consumer's STATE (stale, superseded,
  unopenable, or an incomplete dataset naming what could not be resolved), kept apart from
  `description`, which says what the consumer is for. Stored unbounded in `catalog.Subscriber`,
  searchable from `GET /lineage/subscribers`, shown in the subscriber list and details.
- `subscribers.<name>.url` (the report URL or workbook path, which already existed) is now searchable
  and shown in the subscriber list and search results. A non-http location renders as plain text
  rather than a link that silently does nothing when clicked.
- Search ranks the warehouse above the reporting layer: `objects` leads every result set and
  `subscribers` trails it, in the API payload, the workbench, and the MCP follow-up plan, because a
  bare term is far more often a table or a column than the name of a report.
- Subscribers are now a surface of the global search (`GET /search/subscribers` and the `subscribers`
  category of `/search/all`), matched on name, type, owner, description, notes, and declaring file.
  A subscriber is neither a database object nor a flow, so searching a report by name previously
  returned nothing and the consumer looked absent rather than unsearched.
- Editor support for subscriber libraries: hover, key completion, and unknown-key diagnostics,
  driven by a new `docs/reference/flow/keys.subscribers.json` key model. The analysis engine
  detects a library by its root `subscribers:` key rather than by file name.
- Initial v3 rebuild scaffold on .NET 9.
- Stateless engine (lightweight mode): infer source schema → introspect target → diff →
  generate DDL → bulk load.
- CSV source reader with type inference.
- SQL Server provider: schema introspection, DDL generation, `SqlBulkCopy` loader, CLR→SQL type
  mapping.
- YAML pipeline loader with mapping/validation.
- `sqlflow` CLI: `validate`, `plan`, `run`.
- Open-source project scaffolding: license, contribution guide, code of conduct, security policy,
  CI, and central package management.

[Unreleased]: https://github.com/TahirRiaz/SQLFlow/commits/master
