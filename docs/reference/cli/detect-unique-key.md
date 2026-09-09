---
id: cli-detect-unique-key
title: sqlflow detect-unique-key
type: cli-command
summary: Discover the minimal column set(s) that uniquely identify a table's rows, answering from declared keys in metadata when possible, else via T-SQL profiling with random sampling and whole-table verification.
keywords:
  - unique key
  - key detection
  - declared keys
  - profiling
  - sampling
  - composite keys
  - cardinality
  - selectivity
cliCommand: detect-unique-key
related:
  - cli-catalog-scaffold
  - cli-healthcheck
  - concept-environment-variables
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Profiling/UniqueKeyDetector.cs
  - src/SqlFlow.Core/Profiling/UniqueKeyModels.cs
  - src/SqlFlow.SqlServer/Profiling/SqlServerUniquenessProbe.cs
---

# sqlflow detect-unique-key

## Synopsis

```bash
sqlflow detect-unique-key --object [db.]schema.table [--source <ref>] [--provider mssql|azdb] \
    [--sample N] [--max-columns K] [--max-candidates M] [--no-verify] [--no-metadata] [--json] [--out <file>]
```

## Description

Finds the minimal column set(s) that uniquely identify a table's rows, with no prior knowledge of its keys. A key the database itself already enforces (a primary key, or an enabled, unfiltered unique index or constraint) is answered straight from the catalog metadata without reading a single row; `--no-metadata` opts out of that fast path. Columns whose types can never form a practical key (LOB and legacy LOB types, `xml`, `sql_variant`, `float`/`real`, CLR types, `rowversion`, non-persisted computed columns) are excluded before any data is read and listed in the report with their reasons. The remaining columns are ranked by how identifying they are; an already-unique single column wins outright, otherwise a composite is grown greedily and then reduced to a minimal key. The command needs no pipeline file: it points at any table (or view) reachable through a connection reference.

Profiling runs as T-SQL against the source, so the source must be SQL Server or Azure SQL. The column list comes from the same catalog introspection every other `--object` command uses; the live measurement then runs on the resolved connection, so both see one source of truth. Profiling queries run without a command timeout, matching the engine's other long-running work.

The same detector backs `sqlflow catalog scaffold --detect-keys`, which fills `keyColumns` in a generated flow from the top confirmed key.

If `--source` embeds a literal credential (rather than a `${...}` or `@alias` reference), the command still runs but prints a warning that the value lands in shell history and names the reference alternatives (`${env:NAME}`, `${keyvault:vault/secret}`, the git-ignored `.sqlflow/env` file).

## Arguments

The command takes no positional arguments. The target table is named with the required `--object` option.

| Argument | Required | Description |
| --- | --- | --- |
| `--object <name>` | yes | The table to profile, as `schema.object` or `database.schema.object`. Bracket-quoted parts (`[My Schema].[My Table]`) are supported; dots inside brackets do not split. Missing or malformed values error with exit 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--source <ref>` | string | `${env:SQLFLOW_SOURCE}` | Connection reference for the table: a `${env:...}` or `${keyvault:...}` reference, an `@alias`, or a literal connection string (a literal carrying a credential keyword such as `Password=` triggers the shell-history warning; a passwordless literal does not). |
| `--provider <p>` | string | `mssql` | Source provider. `mssql` (alias `sqlserver`) and `azdb` are accepted; `mysql`, `postgres`, and `oracle` are rejected because profiling is T-SQL. Any other value errors with `Unknown --provider`. |
| `--sample <N>` | int | auto | Working-set size for the search. Absent: tables over 2,000,000 rows are auto-sampled at 500,000 rows, smaller tables get a full scan. `0`: force a full scan. Positive `N`: profile a random sample of roughly `N` rows (only applied when the table is larger than `N`). Tables are sampled with `TABLESAMPLE` page sampling, which reads only the sampled pages; views (and a page sample that returns nothing on stale statistics) fall back to a Bernoulli row filter, one streaming pass in which every row has the same selection chance. Either way the sample is random, never a physical prefix, so the actual sampled row count is approximate. Negative or non-numeric values fall back to auto. |
| `--max-columns <K>` | int | 4 | Widest composite key to consider. `0` is clamped to 1; negative or non-numeric values fall back to the default 4. |
| `--max-candidates <M>` | int | 5 | Maximum number of ranked candidates in the report. `0` is clamped to 1; negative or non-numeric values fall back to the default 5. |
| `--no-verify` | flag | off | Skip the whole-table confirmation of sampled candidates. Returns fast, sample-only candidates marked unverified. Has no effect on a full scan, where every measurement is already exact. |
| `--no-metadata` | flag | off | Ignore keys the database declares (unique indexes/constraints) and always profile the rows. Without it, a declared key is reported instantly from the catalog with no data read. |
| `--json` | flag | off | Print the `UniqueKeyReport` as JSON to stdout instead of the text report. |
| `--out <file>`, `-o <file>` | string | none | Write the `UniqueKeyReport` JSON to a file. Without `--json`, stdout gets a `Wrote unique-key report to <file>` confirmation; with `--json`, the same JSON is also printed. |

## Behavior

The algorithm lives in `src/SqlFlow.Core/Profiling/UniqueKeyDetector.cs`; the SQL Server measurement lives in `src/SqlFlow.SqlServer/Profiling/SqlServerUniquenessProbe.cs`.

1. **Catalog first.** One metadata batch resolves the object before any row is read: declared unique keys (enabled, unfiltered, non-hypothetical unique indexes and constraints), each column's key eligibility by type, and a near-exact row count from the partition metadata. When a declared key exists (and `--no-metadata` was not given), it is reported as `UNIQUE (declared)` and the command finishes without profiling: the answer on any table, at any size, in metadata time. A filtered or disabled unique index proves nothing about the whole table and is ignored.
2. **Column eligibility.** Columns typed `varchar(max)`/`nvarchar(max)`/`varbinary(max)`, `text`/`ntext`/`image`, `xml`, `sql_variant`, `float`/`real`, CLR types (`geography`, `geometry`, `hierarchyid`), `rowversion`, and non-persisted computed columns are excluded from the search up front and listed in the report with reasons. On a wide table this shrinks the search before the first data query; it also keeps types that cannot be compared as keys out of the measurement SQL entirely.
3. **Working set.** When sampling, a random sample of the eligible columns is materialized once into a session temp table, and every measurement runs against that same fixed set, so the search is coherent. The sample is never a physical prefix: a table clustered by date would otherwise eject globally-identifying columns as prefix constants and chase per-prefix false keys. Tables use `TABLESAMPLE` page sampling (only the sampled pages are read); views, and a page sample that returns nothing on stale statistics, use a Bernoulli row filter (one streaming pass, uniform selection chance). The sampling decision itself uses the partition-metadata row count, so a huge table never pays an exact `COUNT` just to learn it must be sampled. The probe holds one open connection for its lifetime because the temp table lives on it.
4. **Per-column cardinality in bounded batches.** Every eligible column's distinct non-null count and null count are measured in scans of at most 16 column sets per query (SQL Server runs each additional `COUNT_BIG(DISTINCT ...)` as its own pass over a shared spool, so an unbounded query over hundreds of columns explodes the plan; bounded batches keep every query cheap). A column that is ever null can never take part in a key and is excluded; a constant column cannot help form one.
5. **Single-column keys win.** Any column with no nulls and one distinct value per row is a key; when one exists, no composite is offered (a composite containing a unique single column is never minimal).
6. **Greedy composites.** Otherwise a composite is grown from up to 3 seeds drawn from the 12 top-ranked columns (the seed pool size is fixed, not CLI-configurable). Each greedy level measures every one-column extension in bounded batches and adds the extension that most increases the distinct-tuple count, stopping when an extension is unique, no extension improves the set, or `--max-columns` is hit. A unique set is then reduced: columns are stripped as long as the set stays unique, leaving a minimal key.
7. **Impossibility bound.** Before any composite search, a necessary condition is checked without a query: `distinct(set) <= product of the columns' distinct counts`, so if even the whole pool's product cannot reach the row count, no subset can be unique. Wide low-cardinality tables are ruled out for free; the most-identifying combination is still measured so the report shows how far off it is.
8. **Verification.** On a full scan the measurements are already exact. On a sample, each surviving candidate is confirmed against the whole table with two `EXISTS` probes (a null row anywhere, a duplicated tuple anywhere), so a reported key is never merely sample-based. `--no-verify` skips this and marks the candidates unverified. When verification rejects a sampled candidate, the exact duplicate count is intentionally not computed; the sample's counts stand, flagged as estimates.
9. **Ranking.** Candidates are ordered unique first, then verified, then fewer columns, then higher selectivity, and capped at `--max-candidates`. Declared keys are ranked narrowest first.

### Text output

The header is `<object>: N row(s)` or, when sampled, `<object>: N row(s), profiled on a sample of S`. Candidates follow under `candidates (most trustworthy first):`, one per line:

```text
   1. [Region, OrderNo]  UNIQUE  selectivity 1
   2. [Region, OrderDate]  not unique (~42 duplicate row(s), sample estimate)  selectivity ~0.9989
```

The status is `UNIQUE (declared)` (from catalog metadata, no rows read), `UNIQUE` (verified), `unique on the sample (unverified)` (`--no-verify`), or `not unique (D duplicate row(s)[, X null row(s)][, sample estimate])`. Counts and selectivity carry a `~` prefix when they are sample estimates. Selectivity is distinct non-null tuples per non-null row, printed with up to four decimals. An empty table prints `no candidate keys.`

When columns were excluded up front, a line lists them with reasons: `excluded from the search: Payload (LOB type (nvarchar(max))), Ratio (imprecise floating-point type (float))`.

A trailing `note:` line explains a declared-key answer, a sampled run (whether the keys were verified), or a no-key outcome: `No unique key was found within the search limits (try raising --max-columns). The top candidate is the closest non-unique combination.` For an empty table the note is `The table is empty; no key can be inferred.`

### JSON output

`--json` and `--out` emit the `UniqueKeyReport` (indented, camelCase, enums as names): `objectName`, `totalRows`, `scannedRows`, `sampled`, `columns` (per-column `column`, `distinct`, `nulls`, `scanned`, `selectivity`), `candidates` (`columns`, `isUnique`, `verified`, `declared`, `distinct`, `nulls`, `rows`, `duplicates`, `estimated`, `selectivity`), `excludedColumns` (`column`, `reason`), and `note`. On a declared-key answer, `scannedRows` is 0 and `columns` is empty (no profiling ran), and `totalRows` may come from the partition metadata rather than an exact count. The model is defined in src/SqlFlow.Core/Profiling/UniqueKeyModels.cs.

## Examples

Detect the key of a staging table using the canonical source connection:

```bash
export SQLFLOW_SOURCE='Server=sql-prod;Database=Staging;Integrated Security=SSPI;TrustServerCertificate=True'
sqlflow detect-unique-key --object stg.Orders
```

```text
stg.Orders: 184230 row(s)
  candidates (most trustworthy first):
   1. [OrderId]  UNIQUE (declared)  selectivity 1
  note: The database metadata declares these column set(s) unique (a primary key or an enabled, unfiltered unique index/constraint), so the rows were not profiled. Use --no-metadata to profile the data anyway.
```

A staging heap with no declared keys is profiled instead and reports plain `UNIQUE` after measurement and verification.

Profile a large Azure SQL table on an explicit 200,000-row sample, allow up to 3-column composites, and save the JSON report:

```bash
sqlflow detect-unique-key --object SalesDb.dbo.OrderLines \
    --source '${env:SALES_DB}' --provider azdb \
    --sample 200000 --max-columns 3 --out .sqlflow/orderlines-key.json
```

```text
Wrote unique-key report to .sqlflow/orderlines-key.json
SalesDb.dbo.OrderLines: 8412990 row(s), profiled on a sample of 200000
  candidates (most trustworthy first):
   1. [OrderId, LineNo]  UNIQUE  selectivity 1
  note: Profiled on a sample of 200000 of 8412990 rows; reported keys were verified against the whole table.
```

Fast exploratory pass without whole-table verification, machine-readable:

```bash
sqlflow detect-unique-key --object dbo.EventLog --sample 50000 --no-verify --json
```

Use the exit code in a script to gate on key existence:

```bash
if ! sqlflow detect-unique-key --object dbo.CustomerFeed >/dev/null; then
  echo "no reliable merge key; falling back to full reload"
fi
```

## Exit behavior

| Code | Condition |
| --- | --- |
| 0 | At least one unique key candidate exists in the report. |
| 2 | No unique key was found (including an empty table). |
| 1 | Errors: missing or malformed `--object`, `object <name> was not found`, `object <name> has no columns to profile`, `detect-unique-key profiles with T-SQL; --provider must be mssql or azdb.`, or an unknown `--provider` value. |

## See also

- [sqlflow catalog scaffold](catalog-scaffold.md): generates a flow file and can fill `keyColumns` from this detector via `--detect-keys`.
- [sqlflow healthcheck](healthcheck.md): the other pipeline-free command that addresses a table directly through `--source`/`--object`.
- [Environment variables](../concepts/environment-variables.md): the `${env:SQLFLOW_SOURCE}` default and connection reference forms.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
