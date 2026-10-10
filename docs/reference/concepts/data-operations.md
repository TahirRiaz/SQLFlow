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
  - column policy
  - sensitive column
  - pii
  - data access control
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
  - src/SqlFlow.SqlServer/Query/SqlColumnAccessExtractor.cs
  - src/SqlFlow.ControlPlane/Api/QueryEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/QuestionExampleEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyGuard.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SearchEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
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
1. POST /api/v1/dataops/queries/prepare   { sql, reference? }
   -> { planId, sql, reference, expiresUtc }      NOTHING HAS RUN

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

Being read-only says nothing about *which* data a SELECT may read - that is a separate check, described below.

### Which datasource a query runs against

`reference` is optional on prepare. Omitted, `DatasourceInference` (`src/SqlFlow.ControlPlane/Api/DatasourceInference.cs`)
parses the statement, takes every table in its FROM clauses (a `COUNT(*)` that names no column still counts),
looks those tables up in the catalog by schema and name, and maps the connection references they were reached
through onto the references active pipelines declare. Exactly one match is used and echoed back as
`reference`; none, or several (the same table known under two datasources), answers **422** naming the
candidates, and the caller names one. It never guesses between them. The same resolver fills the datasource
for PowerAI's confirmed examples and retrieval matches, where the lineage object keys (whose first segment is
the connection reference) are consulted before the SQL. An inferred reference passes the same known-reference
gate an explicit one does, so inference cannot reach a connection the estate does not declare.

Registered objects come first. A table or view that a [schema registration flow](../flow/sch.md)
(`flowType: sch`) registered carries a `Registers` edge to that flow, and an active registration's source server
is where its data is fetched from: inference follows those edges before anything else (for the caller's object
keys, then for the tables the SQL names), and when every registered object lives in one database it also fills
the query's `database`, so the query runs where the objects were registered even if the connection opens
elsewhere. A registration's connection is therefore a declared datasource like any pipeline's.

The known-reference gate is one check, `DatasourceInference.IsDeclaredAsync`, used by prepare, by confirming an
example with an explicit datasource, and by the datasource compute tasks: a whole `${...}` reference passes only
when an **active** pipeline declares it as its source or target (an `@alias` resolves against the node's registry
instead). A deactivated pipeline no longer opens its connection to ad-hoc queries.

### Running a query from the chat GUI

The two-step surface above is not MCP-only: the chat thread (`docs/reference/guides/chat-assistant.md`) can
run a `sql`-fenced block the assistant hands back, through the *exact same* prepare/run pair, one call each,
never a third path. The SQL is already fully visible in the block, so clicking Run is the person's approval;
there is no second confirmation dialog, the same reasoning `auto_run_trusted_match` uses for a confirmed
example (Section "Confirming an answer" of the chat guide) - a click on text a person already read is not a
step that benefits from a second click on the same text. The click names no datasource; prepare works it out
(see "Which datasource a query runs against" below), and a picker (filtered to `resolvable` datasources only)
appears only when prepare answers 422 because it cannot tell.

The result renders as a fitting chart (a stat tile for one row, a bar for one category column against one
measure, a line for a date column against up to four measures, small multiples instead of a shared axis when
those measures' scales are far apart) with a table always one click away, or a table alone when no chart
shape fits; `gui/src/features/chat/QueryResultView.tsx` is the classifier. Gated by `dataOpsRunQuery` on
`GET /api/v1/chat/capabilities` (mirroring `DataOps:Enabled`), so a deployment with the surface off shows no
Run button rather than one that can only answer 403.

## Column policy: what the assistant may read at all

Read-only is a statement-shape guarantee: it says a query cannot write, not that every column it names is fair
game. `CatalogColumnPolicy` (the `ColumnPolicy` table; see [Shadow catalog](shadow-catalog.md#column-access-policy))
is a per-column, admin-authored **allow-list**, orthogonal to the `DataOps` switch and the RBAC scopes: it does
not gate the surface, it narrows what the surface may touch. The model is **default-deny**: a column with no
policy row at all - never reviewed - is exactly as blocked as one an admin has explicitly denied. Policy rows are
per column; the bulk route below writes one for every column of an object at once. The allow-list is also the
[semantic layer](semantic-layer.md)'s membership: an object with at least one allowed column is in the layer, and
a policy row carries the column's semantic `description` and `synonyms`.

Default-deny also applies one level up, to the table itself: a query naming a table, view, or synonym the
catalog has no `CatalogObject` record for at all has no allow-list to check it against, and is refused outright
rather than passed through unchecked. This is what closes the gap a blacklist could not - a synonym pointed at a
sensitive table, or any other object type the harvester does not track, can never "just not have a policy row"
and slip through, because the same default-deny rule that blocks an unreviewed column also blocks an unresolved
table.

An admin sets a column's allow state through the admin-scope API, never through a sync:

| Route | Purpose |
| --- | --- |
| `GET /api/v1/powerai/column-policies` | Every column currently NOT allowed, across the whole catalog - denied, or never reviewed |
| `GET /api/v1/powerai/column-policies/objects/{key}` | Every column of one object with its current allow state |
| `PUT /api/v1/powerai/column-policies` | Full upsert of one column's row (`objectKey`, `columnName`, `isAllowed`, `reason`, `description`, `synonyms`) |
| `PUT /api/v1/powerai/column-policies/objects` | Allow or deny every column of one object (`objectKey`, `isAllowed`), keeping each row's reason and annotations |

The GUI edits all of this on the AI knowledge page (`/semantic-layer`, Admin; the old `/column-policies` link
redirects there).

A column that is not allowed disappears from every surface that could otherwise teach an assistant it exists or
let it read it:

- **Schema discovery**: the chat assistants' only schema readers are the semantic layer tools
  (`get_semantic_layer`, `search_semantic_layer`, `list_semantic_tables`, `describe_semantic_table`), which serve
  nothing outside the allow-list, not even inside a key, join, measure, or example query; see
  [Semantic layer](semantic-layer.md). The raw readers stay filtered too: `search_schema`'s `columns` surface
  (`SearchEndpoints.ColumnsQuery`), the object's paged column list (`GET /api/v1/lineage/objects/columns`), and the
  object dossier's column list (`GET /api/v1/lineage/objects/dossier`, behind `describe_object`) show only columns
  with an `IsAllowed` row.
- **Execution**: `ColumnPolicyGuard.EnsureAllowedAsync` checks every SELECT that reaches `prepare_query`,
  `confirm_question`, or `auto_run_trusted_match` against the allow-list, and refuses one that would read a
  column not on it - refuses it even unnamed, if a `SELECT *` (bare or table-qualified) would expose it. This
  runs in addition to, not instead of, `ReadOnlyQueryGuard`; both must pass. For the chat assistants, a
  `check_duplicate_keys` or `compare_baseline` task is checked the same way, as the SELECT it would read; see
  [Semantic layer](semantic-layer.md#the-assistant-surface).

`confirm_question` matters here as much as `prepare_query`: a confirmed example is precedent every future
similar question can be matched against and auto-run, so a not-allowed column must be refused when an example
naming it is stored, not only when it is later run. And because a column's allow state can change *after* an
example was confirmed, `auto_run_trusted_match` re-checks the policy immediately before every run, even for an
example that passed the check when it was confirmed.

### How a query is checked

`SqlColumnAccessExtractor` walks the already-parsed SELECT and resolves every (table, column) pair it touches.
Table references resolve to catalog objects by schema/name (case-insensitive; an unqualified reference matches
any schema) - never by which datasource the query happens to be prepared against, because a policy is set once
per object and must hold regardless of which connection reaches it. A common table expression's own name is
recognized from the statement's `WITH` clause and exempted from table resolution (it is never a catalog object),
but every real table its body reads is still resolved and checked normally.

The extractor is deliberately **fail-closed**, matching `ReadOnlyQueryGuard`'s own stance that a refused query
that was actually safe costs a rewrite, while an accepted one that was not costs data:

- Every non-CTE table name the statement reads must resolve to at least one `CatalogObject`; if any does not,
  the whole query is refused before any column is even checked.
- A column reference it cannot bind to exactly one table with certainty - an **unqualified column**, or a
  qualifier that matches no alias or table it saw - is attributed to **every** table the statement reads,
  rather than dropped or guessed at. If any of those tables lacks an allow-list row for a column by that name,
  the query is refused, even though the column the author meant might belong to an unrelated table.
- A bare `SELECT *` is recorded as reading **every column of every table** in scope; `alias.*` is recorded as
  reading every column of just that table. Either is refused outright unless every column of that table has an
  `IsAllowed` row, since naming individually allowed columns is the only way past the block.
- Only a flat, whole-statement alias map is built (every `FROM`/`JOIN` in the statement and in any subquery it
  contains, all at once). An alias this pass cannot place falls back to the same fan-out as an unqualified
  column.

This means the guard can occasionally refuse a query that, read carefully, would not actually have touched a
not-allowed column (an unqualified column name that happens to collide with a not-allowed one on an unrelated
joined table). That is accepted by design: guessing narrower here would risk the opposite mistake.

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
| `GET /api/v1/powerai/column-policies` | Every column currently NOT allowed, across the whole catalog (admin scope) |
| `GET /api/v1/powerai/column-policies/objects/{key}` | One object's columns with their allow state (admin scope) |
| `PUT /api/v1/powerai/column-policies` | Allow or deny one column, with its annotations (admin scope) |
| `PUT /api/v1/powerai/column-policies/objects` | Allow or deny every column of one object (admin scope) |

## MCP tools

- `dataops_capabilities` - call first; reports whether the surface is enabled here
- `prepare_query` - step 1: validate a SELECT, get the exact statement plus a token. Nothing runs. Refused if
  the statement would read a column not on the allow-list, or names a table the catalog has no record of
  (see "Column policy" above). The response carries `approvalFormat`, how to offer the statement to a person
- `run_query` - step 2: redeem an approved token and return the rows, with `answerFormat`, `sqlIntro`, `sqlBlock`,
  and (when the MCP server has `SQLFLOW_GUI_URL`) `chartLink` for laying out the answer
- `auto_run_trusted_match` - run a trusted saved answer's SQL with no fresh approval, under the deployment's
  AutoRun row and timeout caps; a successful result carries the same layout fields as `run_query`
- `check_duplicate_keys` - the duplicate check, including the ask-back path
- `compare_baseline` - inventory, schema, or data comparison

The semantic layer tools (`describe_semantic_table` above all) never surface a column outside the allow-list, in
any field, so composing SQL from what they return cannot name one by accident. They are the chat assistants'
schema surface; `search_schema`'s `columns` surface and `describe_object`'s column list are filtered the same way for other MCP
clients.

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

These three tools, and `describe_object`, are not given to the GUI or Slack chat assistants. Those assistants
compose SQL from `describe_semantic_table`, which serves the same key and the same discovered joins (through
the same relationship folding), restricted to allow-listed columns and joined by admin-declared relationships;
see [Semantic layer](semantic-layer.md). The tables below still describe the tools for other MCP clients.

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
