# SQLFlow internals wiki

The catalog of every page in this wiki. Read this first when answering a question against the
wiki, then drill into the pages it points at.

This wiki is written and maintained by an LLM agent, not by hand. Its conventions, and the
ingest / query / lint workflows that keep it current, live in the "Internals Wiki" section of
[CLAUDE.md](../../CLAUDE.md). See [log.md](log.md) for the chronological record of what has been
ingested and when.

**In a hurry?** [Pattern catalog](maps/pattern-catalog.md) maps a data-engineering problem onto the
SQLFlow shape that solves it and the production flow that proves it.

## What belongs here, and what does not

This wiki explains **why SQLFlow is the way it is, how the pieces fit together, and which shape
solves which problem**. It is the synthesis layer over three raw sources: the code in `src/`, the
historical design documents under `docs/`, and the production pipeline estate.

It is not the reference manual. [docs/reference/](../reference/) documents **what** every CLI verb,
YAML key, and concept does, derived from and verified against the code. A wiki page never restates
what a reference page already says. It links to it and adds what the reference cannot carry:
rationale, rejected alternatives, cross-cutting narrative, problem-to-shape mapping, and history.

Both trees are indexed into one [manifest.json](../reference/manifest.json) by
[build_manifest.py](../reference/build_manifest.py) and embedded into the MCP server at compile time
by [tools/sqlflow-mcp/build.rs](../../tools/sqlflow-mcp/build.rs), so every page here is reachable
through `search_docs`, `get_doc`, `related_docs`, and `list_docs`. Filter by the `type` values below
to narrow a search to one kind of page.

| Directory | `type` | Answers |
| --- | --- | --- |
| `narratives/` | `narrative` | How do I actually do X, end to end? (code-first recipes) |
| `patterns/` | `pattern` | Which shape solves this data-engineering problem? |
| `decisions/` | `decision` | Why is it this way, and what was rejected? |
| `incidents/` | `incident` | What broke, and what generalizes from it? |
| `maps/` | `map` | Where does knowledge live, and which parts are stale? |

## Pages

### Maps

- [Pattern catalog](maps/pattern-catalog.md) - problem-first index over the 751 production
  pipelines, mapping each recurring problem to the construct that solves it. **Start here.**
- [Census drift map](maps/census-drift.md) - nine key paths the engine accepts that
  `keys.api.json` does not declare, and the enum value it documents one version behind.
- [Design document drift map](maps/design-doc-drift.md) - which documents under `docs/` describe
  shipped behavior, which are historical design intent, and where the unbannered gaps are.

### Patterns: acquisition

- [Authenticating to a third-party API](patterns/api-authentication.md) - the five auth shapes, the
  four token-endpoint variations production forced, and why credentials are always references.
- [Walking a paged endpoint](patterns/api-pagination.md) - the five strategies, how each detects the
  end, and why a repeating cursor needs keyset instead.
- [Fetching one endpoint many times](patterns/api-fanout.md) - date windows, id lists, discovered
  ids, batching, and the retired-entity blind spot.
- [Resuming where the last run stopped](patterns/api-resume-and-watermarks.md) - the three watermark
  sources, durability across redeploys, and per-entity resume points.
- [Surviving a third-party API](patterns/api-resilience.md) - retries, rate limits, tolerating dead
  ids, and the SSRF guard the allowlist cannot widen.

### Patterns: landing and ingestion

- [Landing raw payloads verbatim](patterns/landing-and-protection.md) - why acquisition does not
  parse, how the path template partitions the lake, and the one deliberate exception for PII.
- [Turning landed files into tables](patterns/file-ingestion-shaping.md) - explode and include
  paths, CSV dialects, dating a file from its name, and pinning what inference gets wrong.
- [Incrementally ingesting a relational source](patterns/relational-incremental.md) - windows with
  overlap, chunked first loads, and narrowing at the source without changing what a filter means.
- [Upserting, keeping history, not breaking a consumer](patterns/upsert-and-history.md) - the merge
  key as the design, temporal versioning, and the compatibility view.

### Patterns: orchestration

- [Ordering a source's flows](patterns/orchestration-and-scheduling.md) - one schedule on the
  anchor, lineage waves, chaining, retiring a flow without deleting it, and the consumer registry.

### Decisions

- [Landing is string-first](decisions/string-first-landing.md) - why every non-Parquet source lands
  as strings and typing is deferred, and what the alternative would have cost.

### Incidents

- [A misspelled or misplaced YAML key does nothing](incidents/ignored-yaml-keys.md) - unmatched
  properties are silently dropped; a live production flow configures a retry knob that is not real.

### Narratives: recipes

Code-first, end-to-end. Each one is a runnable shape with generic table and column names.

- [How flows chain through the lake](narratives/chaining-flows-through-the-lake.md) - the two joints
  that compose every pipeline, and how lineage derives the waves from them. **Read this first.**
- [Connecting any source](narratives/recipe-connect-any-source.md) - every integration in one table:
  which flow kind fetches it, the minimum YAML, and the rule when two kinds could both work.
- [A REST API into the warehouse](narratives/recipe-api-source.md) - the three files, the schedule
  that fires them in order, and the commands to verify each stage.
- [Vendor files into the warehouse](narratives/recipe-vendor-file-source.md) - an SFTP drop or a
  `cpy` bridge, the `output` block lineage needs, and one file feeding several tables.
- [Another database into the warehouse](narratives/recipe-database-source.md) - browse, scaffold,
  then split the first load from the daily delta.
- [Choosing an incremental load](narratives/recipe-incremental-load.md) - a decision tree, and the
  two ways an incremental silently does nothing.
- [Staging to silver](narratives/recipe-staging-to-silver.md) - the typed view is the contract:
  writing it, keeping a rebuilt table's old shape, and running two eras behind one name.
- [Building a dimension and its surrogate keys](narratives/recipe-dimension-and-surrogate-keys.md) -
  minting keys in a key table, projecting into `edw`, and the snapshot trap that truncates history.
- [Several regions into one table](narratives/recipe-fan-in-shared-target.md) - the discriminator in
  the merge key and the scoped probe that stops the writers starving each other.
- [Backfilling and replaying](narratives/recipe-backfill-and-replay.md) - the flags that reach past a
  watermark, and why a backfilled table stays invisible to the flow above it.
- [Getting data back out](narratives/recipe-export-and-deliver.md) - `exp` to files, `trl` to
  documents, `inv` to another system, and the subscriber registry that answers "what breaks".
- [Noticing a wrong-but-successful run](narratives/recipe-quality-and-monitoring.md) - anomaly checks
  and the plain SQL assertions a model will not write for you.
- [Seeing what a flow will do](narratives/recipe-inspect-and-debug.md) - the read-only ladder, and
  the ordered checklist when a flow loads nothing.

[docs/flattener-memory-postmortem.md](../flattener-memory-postmortem.md) is an existing postmortem
that has not been ingested into this wiki.

## Coverage

Seeded 2026-09-09 with the structure, the lint tool, and the pattern catalog harvested from the
production estate. The pattern pages cover the constructs that estate actually exercises; a
construct SQLFlow supports but nothing in production uses may have no page yet. Absence of a page
means the topic has not been ingested, never that it is out of scope.
