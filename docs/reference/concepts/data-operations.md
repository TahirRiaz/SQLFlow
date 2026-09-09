---
id: concept-data-operations
title: "Data operations: ad-hoc business queries, duplicate keys, and baseline comparison"
type: concept
summary: The prepare-confirm-run query surface, the duplicate-key check, and baseline comparison, behind the ControlPlane DataOps switch.
keywords:
  - dataops
  - text to sql
  - ad-hoc query
  - duplicate keys
  - baseline comparison
  - linked server
  - migration reconciliation
  - compute task
related:
  - concept-control-plane
  - concept-shadow-catalog
  - concept-upsert-and-change-detection
  - concept-provenance-and-row-keys
sourceRefs:
  - src/SqlFlow.Core/Quality/DuplicateKeyModels.cs
  - src/SqlFlow.Core/Query/QueryModels.cs
  - src/SqlFlow.Core/Comparison/BaselineComparisonModels.cs
  - src/SqlFlow.Core/Comparison/SqlFragmentGuard.cs
  - src/SqlFlow.SqlServer/Quality/SqlServerDuplicateKeyProbe.cs
  - src/SqlFlow.SqlServer/Query/ReadOnlyQueryGuard.cs
  - src/SqlFlow.SqlServer/Query/SqlServerQueryRunner.cs
  - src/SqlFlow.ControlPlane/Api/QueryEndpoints.cs
  - src/SqlFlow.SqlServer/Comparison/SqlServerBaselineComparer.cs
  - src/SqlFlow.Execution/ComputeTaskExecutor.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
---

# Data operations

Three live, interactive capabilities that run against the warehouse from the control plane: **ad-hoc business
queries** (answering a question with real numbers, behind a human confirmation), the **duplicate-key check**,
and the **baseline comparison** that proves a V3 migration against the old production estate.

All three are **read-only**. A query is parsed and refused unless it is a single SELECT, then executed inside
a transaction that is always rolled back. The duplicate check groups and counts. The comparison reads both
estates and writes nothing but a session temp table in `tempdb`. Those properties are enforced by construction
and asserted in tests, which is what makes the surface safe to hand to an assistant.

All three are **off by default**, behind one switch.

> **Not a DBA surface.** This deliberately does not offer index rebuilds, statistics updates, compression, or
> any other warehouse maintenance remediation. The four warehouse-health DMV probes (`missingIndexes`,
> `statisticsHealth`, `indexUsage`, `topQueries`) are a separate, older feature that backs the insights
> recommendations; they are NOT part of this surface and are not gated by its switch.

## The switch

```
ControlPlane__DataOps__Enabled=true
```

With it off, `POST /api/v1/datasources/tasks` refuses the `duplicateKeys`, `compareBaseline` and `runQuery` operations, both query endpoints answer 403,
with a 403 naming the setting, and `GET /api/v1/dataops/capabilities` reports `enabled: false` so a GUI or an
assistant explains the situation instead of showing a failing button. Nothing else in the product changes,
and in particular the insights dashboard is unaffected.

The comparison additionally needs its linked servers allowlisted. A linked-server name becomes an identifier
in generated SQL and a route into another estate, so it is configuration, never something a request chooses:

```
ControlPlane__DataOps__Comparison__LinkedServers__0=old-dwh-prod
ControlPlane__DataOps__Comparison__LinkedServers__1=old-pre-prod
ControlPlane__DataOps__Comparison__LinkedServers__2=old-sqlflow-prod
ControlPlane__DataOps__Comparison__LinkedServers__3=OLDPROD
ControlPlane__DataOps__Comparison__DefaultLinkedServer=old-dwh-prod
```

`Databases` narrows it further to named databases on those servers; left empty, any database the linked
server's own login can reach is permitted, which is the usual case because the linked server is the boundary.

## How it executes

Both ride the existing ad-hoc compute queue, so nothing new was invented for transport:

1. `POST /api/v1/datasources/tasks` (the `operate` scope) validates the request at the trust boundary and
   writes a `CatalogComputeTask` row. Only a connection **reference** travels; a secret never does.
2. Whichever worker node can reach the source claims the row (`RunWorker.DrainComputeAsync` drains compute
   ahead of flow runs, because compute is interactive) and executes it through `ComputeTaskExecutor`.
3. The result lands on the same row. `GET /api/v1/datasources/tasks/{id}?waitMs=20000` long-polls it.

The control plane never opens a connection to a datasource. Neither check carries a wall-clock deadline: an
anti-join over a billion rows legitimately outruns any deadline safe for an interactive browse, so they stay
cancellable and are backstopped by the queue's running-task expiry, exactly like `detectUniqueKey`.

Every task row records `RequestedBy` and the full arguments, so this surface is auditable by construction.

## Ad-hoc business queries

A question like "what were the daily boarding totals on route 5200 last month" needs real numbers, not SQL to
paste elsewhere. That runs here, in **two steps**, and the split is the whole design:

```
1. POST /api/v1/dataops/queries/prepare   { sql, reference }
   -> { planId, sql, expiresUtc }      NOTHING HAS RUN

2. (the caller shows that exact sql to a person and gets agreement)

3. POST /api/v1/dataops/queries/{planId}/run
   -> 202 + taskId, then poll the task for the rows
```

The confirmation is **enforced, not requested**. The run endpoint takes a token and never a statement, so the
only executable SQL is SQL that was first prepared and handed back to be shown. A client that wanted to skip
asking has nothing to skip to. The plan row is:

- **single-use** - redeeming it twice returns 409, so running the same query again means preparing it again
  and each execution is separately approved;
- **short-lived** (15 minutes) - an approval is a decision about a moment, not a standing permission, so a
  token left in a transcript is not a key;
- **attributable** - it records who prepared what and links to the task that ran it.

### Read-only, proved twice

`ReadOnlyQueryGuard` **parses** the statement with the same T-SQL parser the lineage extractor uses. That
distinction matters: a denylist over text loses to casing, comments, whitespace and nesting, while a parse
tree either contains a write node or it does not. `select 1; dRoP tAbLe x` and `SELECT 1 /* c */ ; DELETE ...`
are both refused as "more than one statement", not by spotting a keyword.

The rule is an allowlist at the top (exactly one batch holding exactly one SELECT) plus a refusal of every
construct that can reach outside the query from *inside* a select: `SELECT ... INTO` (a SELECT that creates a
table), a procedure call, `OPENQUERY` / `OPENROWSET` (which run statements this cannot inspect, possibly on
another server), and `xp_` / `sp_` functions.

It is validated at **both** ends: at prepare in the control plane, and again on the node before execution,
because the queue row is data from the database. On top of that, the query runs inside a transaction that is
**always rolled back**, so even a statement that somehow passed the parser leaves nothing behind. Belt and
braces is warranted where the cost of being wrong is data.

### Bounds

Rows stop at `maxRows` (default 200, max 5000) with the result marked `truncated`; the read STOPS there rather
than pulling the rest and slicing. A command timeout applies (default 120s, max 600s), and an oversized cell is
trimmed with a marker so one `varchar(max)` column cannot swamp a small result.

`truncated` is the field a caller must read before describing an answer: a truncated result is a page, and
summing a page gives a confidently wrong total.

### Composing the SQL

Compose from the metadata, never from guessed names: `get_table_key` for the grain, `get_table_joins` for the
ON clauses, `describe_object` for the columns. That is what the join graph below exists for.

## Duplicate keys, and the key it uses

The `duplicateKeys` operation answers "does this table hold more than one row per key". The key it
groups by is the one the **table itself declares**, in this order:

1. **SQLFlow's own `NCI_KeyColumn`** business-key index, which `CanonicalIndexPlanner` creates on every target.
   This is the key the load MERGES on, so a duplicate against it is a real defect. Matched by prefix, because
   legacy SQLFlow suffixed the name with a table hash and a table ported from old production still carries
   that form. It is chosen **whether or not it currently enforces uniqueness**: a non-unique or disabled
   variant is precisely the case where duplicates can have accumulated.
2. A **primary key that is not a bare identity**.
3. Any other **unique index or constraint**, narrowest first.

A surrogate identity key is **never** used. SQLFlow appends one to every target it creates, so a naive "group
by the primary key" check would group by a column that is unique by construction, report zero duplicates on
every table in the estate, and prove nothing.

Two details decide how a result must be read, and the report states both:

- **A filtered key index** (SCD2's `NCI_KeyColumn`, `WHERE [flag] = 1`) is unique only among current rows. The
  check applies the same predicate, so historical versions are not counted as duplicates.
- **An enabled, unfiltered UNIQUE index** makes duplicates impossible. A zero is then *guaranteed, not
  measured*, and the report says so rather than dressing up a tautology as a finding.

### When the table declares no usable key

The action **asks**. It does not guess, because a duplicate check run against the wrong key answers
confidently and wrongly, which is worse than not answering. The task succeeds carrying a `question`:

```jsonc
{
  "question": {
    "prompt": "Which columns identify one real row of arc.Ferde_Passeringer? ...",
    "parameter": "columns",
    "options": ["Dato", "Sted", "Klokkeslett", "..."]
  }
}
```

A client presents that to a person, collects an answer, and re-runs with `columns`. A client must check
`question` before reading `findings` as a verdict.

## Baseline comparison

Compares the current V3 estate against the OLD production baseline through an allowlisted linked server. The
aggregation and the anti-join run **server-side** via `OPENQUERY`, so only the answer travels and a
billion-row table can be compared at all.

Three modes, meant to be climbed in order:

- **`inventory`**: every table in a schema on either side, with row counts from partition metadata (exact for
  a settled table, and free). Says which tables disagree at all.
- **`schema`**: one object's columns **position by position** - name, type with length and precision,
  nullability, identity. Walking positions rather than matching names is deliberate: column ORDER is part of
  the contract for any consumer doing `SELECT *`. The verdict distinguishes the three cases that matter -
  identical (a direct transfer is legal), same column set in a different shape (the case a **compatibility
  view** under the old name is for), and a different column set (a mapping problem no view alone solves).
- **`data`**: one object's rows. A count decomposition, a **bidirectional** key anti-join, and value parity
  across the shared keys.

### The logical key

Data mode requires `keyExpressions`: the expressions that identify one real-world reading **on both estates**.
Not the surrogate primary key, which each estate assigns independently and which therefore proves nothing.
Establish it with a person before running; a wrong key invalidates every number below it.

The comparison is built to refuse the mistakes that make a reconciliation lie:

- **Physical rows are not the comparison.** A table with duplicates on one side can hold exactly the same
  readings as the other and still report a different `COUNT(*)`. The report decomposes physical rows into
  distinct logical keys plus duplicates, and says which number matters.
- **Both anti-join directions, always.** A table that is "sometimes more, sometimes less" is the normal case
  after a migration, and a one-directional check reads it as clean.
- **Each side is collapsed to one row per key** before value parity. Without that the join is many-to-many
  across duplicates and inflates every mismatch below it.
- **Value parity uses `EXCEPT`**, which is NULL-safe, so a column legitimately NULL on both sides is not a
  mismatch on every row.
- **Provenance and audit columns are excluded by default** (`%\_DW`): they carry the load instant, which
  legitimately differs between estates.
- A column whose mismatches are **all** "current is NULL where baseline was not" is called out as the
  empty-string-versus-NULL landing difference, not lost data. It is the most common false alarm in this
  migration.

### The fragment guard

`keyExpressions` and `where` cannot be parameters: they are projected and grouped, not compared to a value, so
they are interpolated into generated SQL. `SqlFragmentGuard` is what makes that safe, and it is an
**allowlist**: a fragment is accepted only when every token is an identifier, a bracketed identifier, a
literal, an operator, or a word on the keyword/function allowlist. That refuses statement terminators, comment
introducers, variables, every DML and DDL verb, and any function call outside a small scalar set - so a
fragment cannot stop being an expression and become a statement. Every identifier is passed bare and quoted by
the builder, so a name carrying its own bracket is refused rather than escaped.

## API

| Route | Purpose |
| --- | --- |
| `GET /api/v1/dataops/capabilities` | Whether the surface is enabled, the operations available, the allowlisted linked servers |
| `POST /api/v1/dataops/queries/prepare` | Validate a SELECT and mint a one-time plan token. Nothing runs |
| `POST /api/v1/dataops/queries/{planId}/run` | Redeem an approved token and queue the query (202 + taskId) |
| `POST /api/v1/datasources/tasks` | `operation: "duplicateKeys"` or `"compareBaseline"` |
| `GET /api/v1/datasources/tasks/{id}?waitMs=20000` | Long-poll the result |

## MCP tools

- `dataops_capabilities` - call first; reports whether the surface is enabled here
- `prepare_query` - step 1: validate a SELECT, get the exact statement plus a token. Nothing runs
- `run_query` - step 2: redeem an approved token and return the rows
- `check_duplicate_keys` - the duplicate check, including the ask-back path
- `compare_baseline` - inventory, schema, or data comparison

## Composing SQL against these tables

The metadata needed to author a correct query is not part of this surface, because it already exists. It is
reached through three tools that each answer ONE question, so a model calls the right one instead of having
to know that a general-purpose aggregate happens to contain the answer:

| Tool | Question | Cost |
| --- | --- | --- |
| `get_table_key` | What identifies one row of this table? | Metadata, instant |
| `get_table_joins` | How does this table join to others? | Metadata, instant |
| `detect_unique_key` | What does the DATA actually support as a key? | Profiles rows on a worker node |

`get_table_key` and `get_table_joins` are projections of the object dossier
(`GET /api/v1/lineage/objects/dossier`), trimmed to one answer each: a narrow question should not spend a
wide answer's worth of context. `describe_object` still returns the whole dossier when a model genuinely
wants everything at once.

The join graph is the part worth understanding. SQLFlow does not rely on declared foreign keys, because a
warehouse rarely has them. `TSqlLineageExtractor` reads the AND-connected column equalities out of every view
and procedure it parses, folding a composite key into a single observation and discarding OR branches and
non-equality predicates as filters rather than join identity. Those land in `CatalogObjectRelationship` with:

- **`origin`** - `Constraint` for an explicit FOREIGN KEY clause, `Join` for a relationship inferred from the
  predicates the code actually joins on;
- **`occurrences`** - how many distinct scripts exhibited it, so the join the estate uses most ranks first and
  a one-off join in a single report does not outrank the canonical path;
- **`tier`** - Declared / Observed / Derived, the provenance of the strongest observation.

### How can I join table X?

`get_table_joins` (`GET /api/v1/lineage/objects/join-paths`) answers that as a search, not a lookup. It
breadth-first walks the relationship graph from one object and returns every ROUTE, each an ordered chain of
hops with a pasteable `ON` clause per hop:

- **With just the table**, it lists everything reachable within the hop budget: what can I join this to, and
  how.
- **With `other`**, it lists the routes to that specific table, *including through a bridge* when the two are
  not related directly. A fact table reaching a second dimension only through the first is the normal shape of
  a star schema, and a one-hop-only answer would wrongly report "no way to join these".

Two design points matter for a caller composing SQL:

- **Rival routes are kept, not deduplicated.** When the codebase joins the same two tables on more than one
  column set (a surrogate `RouteId` in most scripts, a natural `RouteNumber` in one), both come back. That is
  a decision for whoever is writing the query, and collapsing it to a single winner would hide a real
  ambiguity behind a confident answer.
- **A route is ranked by its weakest link.** `minOccurrences` is the least-used hop in the chain, because a
  chain is only as canonical as its flimsiest step. Ordering is fewest hops, then that weakest link, then a
  declared `Constraint` ahead of an inferred `Join`.

Breadth-first is what makes a direct join always beat a chain to the same table. Searches are bounded (hops,
routes, and objects expanded) and set `truncated` when a bound cut the walk, so a short list is never mistaken
for a complete one. When no route exists at all the reply says so in words: nothing in the codebase joins
those tables, and a join condition guessed from matching column names is not a substitute.
