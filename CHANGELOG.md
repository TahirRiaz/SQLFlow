# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- Only the person's command moves the assistant between data mode and estate mode. `!data` or a bare `!` starts data
  mode and `!exit` or `!estate` ends it; a question's wording, a request in words and a follow-up of the other kind no
  longer move it, and the assistant no longer switches by itself. Every MCP session answers only the tools of its mode:
  a session opened in a mode (`?mode=`) lists no switch, and one that names no mode lists every tool but refuses the
  other mode's, switching only when `start_data_mode` or `end_data_mode` is given a message that begins with the
  command (their new `message` argument). The GUI chat and the Slack bot read the mode before a run and store it with
  the answer; a run is asked once, and the chat stream's `restart` event and `AssistantEvent.Restarted` are gone. One
  grammar for the command, held by `tools/sqlflow-mcp/tests/assistant-commands.json` in the MCP server, the control
  plane and the GUI, reads `!exit.` as the command and `!exit-interview` as a question; the Slack bot reads a command
  after a `<@U123|name>` mention too, and a saved question is cleaned by the same grammar.
- The MCP server lays out the SQL in its answers. Every `sql` field of a tool result, and every field whose name ends
  in `Sql` (`sourceSql`, `reportSql`, `suggestedSql`, `sampleSql`, `failedStatementSql`), comes a clause to a line with
  two spaces to an indent, and so does the fenced `sqlBlock` of a query answer; until now an editor's assistant or
  Slack showed a composed query, a visual's translated query or an engine statement as the one line the control
  plane holds. Only the whitespace between tokens changes: names, literals, comments and keyword case stay as
  written, and a statement that cannot be laid out without changing a token (checked against its own T-SQL tokens)
  is returned exactly as it was. Layout is `sqlformat` (`tools/sqlflow-mcp/src/sql_format.rs`), applied in `json_str`,
  so a host module's answers get it too.
- The data-mode command is `!data` (or a bare `!`); `!cwd` is gone, since an editor's assistant such as Claude Code
  runs a prompt that starts with `!` as a shell command. The MCP server gained `get_mode` (the session's mode, how it
  was set, and whether the session lists every tool) and `start_data_mode` beside `end_data_mode`, so a person can
  switch in words, and an assistant run is asked again in the other mode whichever way it switches, at most once. The
  GUI chat stores the mode each answer ended in (`catalog.ChatMessage.AssistantMode`, migration
  `AddChatMessageAssistantMode`) and the Slack bot puts it on its reply as message metadata, so a switch the assistant
  made holds for the next message. `IAssistantGateway.AskAsync` returns an `AssistantAnswer`, the text and the mode.
- AI context is turned on with one switch, `ControlPlane__AiContext__Enabled=true`, named as the GUI names the
  feature: it covers a question's query (prepare/run and the chat's Run button), saved answers, and running a
  trusted saved answer straight away, which is now on by default (`ControlPlane__AiContext__AutoRun__Enabled=false`
  turns it off). The `ControlPlane:PowerAI` section is gone: `PowerAI:Retrieval:Enabled` is `AiContext:Enabled`,
  `DefaultTopK` and `RankThreshold` are `AiContext:SavedAnswers:TopK` and `RankThreshold`, `AutoRun:*` and
  `ReportExtraction:*` moved under `AiContext`, and `AiContext:ExpandSynonyms` follows the Anthropic key unless set,
  so it no longer stops startup. `ControlPlane:DataOps:Enabled` now gates only the duplicate-key check and the
  baseline comparison; queries no longer need it. The chat capability `dataOpsRunQuery` is `runQuery`.

### Added
- Azure Storage is reached over the account's Blob or Data Lake (`dfs`) endpoint, whichever the host can reach. File
  sources, landing, exports, copies and SFTP transfers decide once per account which endpoint answers (a private
  endpoint first) and move to the other when the network refuses a request before the service processed it, so a
  network that wires only the `dfs` endpoint no longer fails with `403 AuthorizationFailure`.
  `SQLFLOW_AZURE_STORAGE_ENDPOINT` (`auto`, `blob`, `dfs`) fixes the choice; `duckdb` and `delta` sources still read
  through DuckDB's Azure extension ([Azure Storage endpoints](docs/reference/concepts/azure-storage-endpoints.md)).
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
- A window- or filter-bounded backfill of an INCREMENTAL landing flow now takes the normal
  `load.resetWhenConsolidated` gate instead of being refused outright. A plain run of such a flow lands
  only its delta, so a slice is the same kind of load: once every direct consumer has a successful run
  after the last load, the backfill resets the landing table and lands the slice alone. Before, every
  backfill appended to rows the consumers already held, and because a plain run resets only after it
  finds new files, a source that had gone quiet never cleared them. A flow that re-lands its whole
  selection on every run (no `incremental:` block) still keeps its rows through a bounded run, since a
  reset there would leave only the slice for a consumer that rebuilds from it. The node states the
  fact on the run-context request (`incrementalLanding`); a node that does not send it is refused as
  before.
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
