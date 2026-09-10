---
id: cli-plan
title: sqlflow plan
type: cli-command
summary: Preview the exact DDL a file flow would run. Reads the source schema, introspects the live target, diffs, and prints the plan without executing anything.
keywords:
  - plan
  - dry run
  - ddl preview
  - schema diff
  - schema delta
  - file flows
cliCommand: plan
related:
  - cli-run
  - cli-validate
  - concept-schema-evolution
  - flow-schema
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Engine/SchemaDiffer.cs
  - src/SqlFlow.Core/Model/Results.cs
  - src/SqlFlow.SqlServer/SqlServerDdlGenerator.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Core/SqlFlowException.cs
  - samples/quickstart/orders.flow.yaml
---

# sqlflow plan

Show the exact SQL a file flow would execute, without changing anything.

## Synopsis

```bash
sqlflow plan <pipeline.flow.yaml> [-v|--verbose]
```

## Description

`plan` answers the question "what will `sqlflow run` do to my target table?" before you run it. It calls `FlowRunner.PlanAsync` (src/SqlFlow.Core/Engine/FlowRunner.cs), which:

1. Resolves the target connection reference through the secret resolver.
2. Reads the source schema from the actual files (`source.columns` stage).
3. Builds the desired table schema by mapping every source column through the type mapper, applying `schema.defaultColumnType` and `schema.overrides`.
4. Introspects the live target table (`target.introspect` stage). This is a real database round trip, so connectivity to the target is required.
5. Diffs desired against actual under the flow's `schema.evolve` policy and generates the DDL that `run` would execute.

Nothing is executed against the database: no DDL is applied, no rows are loaded, no state is saved, and no run artifacts are written under `.sqlflow/runs/`. The result is a `FlowPlan` record (src/SqlFlow.Core/Model/Results.cs) carrying `Flow`, `SourceColumns`, `Desired`, `Actual`, `Delta`, `DdlStatements`, and `Trace`; the CLI prints a summary of it.

`plan` supports file flows only (the default document kind: CSV, JSON, XML, XLS, or Parquet into SQL Server). For every other document kind (`flowType: ing`, `exp`, `sp`, `inv`, `hc`, `scm`, `batch`) the command prints this to standard error and exits 1:

```text
ERROR  'plan' supports file flows; ingestion, export, and stored-procedure work is determined at run time against the live source.
```

The document loads through the same loader as `validate` and `run` (src/SqlFlow.Execution/DocumentLoader.cs): a relative `source.location` resolves against the flow file's own directory, and a connection value that looks like an embedded credential produces a secret-hygiene warning on standard error. The git-ignored `.sqlflow/env` file (searched from the flow file's directory upward) is applied before anything resolves; the process environment always wins.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<pipeline.flow.yaml>` | yes | Path to a file-flow pipeline document. Any other flow kind is rejected with exit code 1. |

## Options

`plan` has no command-specific flags. The global CLI flags apply:

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-v`, `--verbose` | switch | off | Debug-level console logging. Also reports on standard error when a `.sqlflow/env` file was applied. |
| `-h`, `--help` | switch | off | Print usage. Exits 0 when the file argument is also present; without it the missing-argument check wins and the exit code is 1. |

## Behavior and output

The printed plan has a fixed shape (`PrintPlan` in src/SqlFlow.Cli/Program.cs):

```text
Plan for '<name>' -> [<schema>].[<table>]
  target : exists | does not exist (will create)
  columns to add : <N>
  load mode      : Append | TruncateLoad
  generated DDL  : (none)   (or each statement, verbatim, indented)
  trace:
    ok    <operation>  <elapsed> ms   (one line per stage; FAIL on a failed stage)
```

- The header prints the flow's `name` and the target's qualified name (`[schema].[table]`).
- `target : does not exist (will create)` means the introspection found no table; the whole desired schema becomes the `CREATE TABLE` statement.
- `columns to add` is the diff's `ColumnsToAdd` count. For a new table that is every column; for an existing table under `evolve: widen` it is only the columns the target lacks.
- `load mode` prints the parsed `LoadMode` enum value: `Append` (YAML `append`) or `TruncateLoad` (YAML `truncate-load`).
- `generated DDL` prints each statement exactly as `run` would execute it, indented four spaces, or `(none)` when the schema already matches. Generation rules (src/SqlFlow.SqlServer/SqlServerDdlGenerator.cs): a missing table gets one `CREATE TABLE` statement; an existing table gets one `ALTER TABLE ... ADD ... NULL;` per new column (columns added to an existing table are always nullable, since there is no default to backfill existing rows with).
- `trace` lists the plan's stages with per-operation timing: `source.columns` then `target.introspect`. Unlike `run` output, there is no `TOTAL` line.

Evolve-policy interaction (src/SqlFlow.Core/Engine/SchemaDiffer.cs):

- `create`: an existing table produces an empty delta, so `generated DDL : (none)` even when the source has new columns.
- `widen`: new source columns become `ALTER TABLE ... ADD` statements.
- `strict`: when the target lacks source columns, the diff throws and `plan` fails with `Schema drift on [<schema>].[<table>]: target is missing N column(s) (...) and the evolve policy is 'strict'.`

An unknown `source.type` fails with `No source reader is registered for source type '<type>'.` (src/SqlFlow.Core/Engine/FlowRunner.cs).

Each `plan` invocation mints a fresh time-ordered run id (`Guid.CreateVersion7()`) and publishes its stage events to the configured flow event sink, but it records nothing durable.

## Examples

Plan the quickstart flow before the target table exists (samples/quickstart/orders.flow.yaml: CSV source, `evolve: widen`, `OrderId` overridden to `BIGINT NOT NULL`, all other CSV columns falling back to `defaultColumnType: varchar(255)`, plus the provenance columns the CSV reader injects by default):

```bash
sqlflow plan samples/quickstart/orders.flow.yaml
```

Representative output (timings vary):

```text
Plan for 'orders' -> [dbo].[Orders]
  target : does not exist (will create)
  columns to add : 11
  load mode      : Append
  generated DDL  :
    CREATE TABLE [dbo].[Orders] (
        [OrderId] BIGINT NOT NULL,
        [Customer] varchar(255) NULL,
        [Amount] varchar(255) NULL,
        [OrderDate] varchar(255) NULL,
        [IsPaid] varchar(255) NULL,
        [FileName_DW] NVARCHAR(4000) NULL,
        [FileDate_DW] DATETIME2 NULL,
        [FileRowDate_DW] DATETIME2 NULL,
        [FileSize_DW] BIGINT NULL,
        [DataSet_DW] DATETIME2 NULL,
        [RowNumber_DW] BIGINT NULL
    );
  trace:
    ok    source.columns              3.1 ms
    ok    target.introspect          41.7 ms
```

Re-plan after the table exists and the source has grown one column (`Discount`), with `evolve: widen`:

```bash
sqlflow plan samples/quickstart/orders.flow.yaml
```

```text
Plan for 'orders' -> [dbo].[Orders]
  target : exists
  columns to add : 1
  load mode      : Append
  generated DDL  :
    ALTER TABLE [dbo].[Orders] ADD [Discount] varchar(255) NULL;
  trace:
    ok    source.columns              2.8 ms
    ok    target.introspect          39.2 ms
```

When source and target already agree, the same command prints `columns to add : 0` and `generated DDL  : (none)`.

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | Plan printed successfully (including the no-op case with no DDL). Also returned by `-h`/`--help` when the file argument is present. |
| 1 | No file argument (usage is printed); the document is not a file flow; or a `SqlFlowException` occurred (invalid YAML, unresolved connection reference, unknown source type, no matching source files, strict-policy schema drift). Errors print `ERROR  <message>` to standard error with secret values redacted. |

## See also

- [sqlflow run](run.md): execute the same plan, then load.
- [sqlflow validate](validate.md): check the document offline, without touching source files or the target.
- [Schema evolution](../concepts/schema-evolution.md): the `create` / `widen` / `strict` policies the diff applies.
- [schema](../flow/schema.md): the flow document's `schema` section (`evolve`, `defaultColumnType`, `overrides`).
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
