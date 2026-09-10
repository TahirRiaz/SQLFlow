---
id: cli-validate
title: sqlflow validate
type: cli-command
summary: Validate any flow document offline. Parses all eight document kinds, prints a per-kind OK line, warns on embedded credentials, exits 0 or 1.
keywords:
  - validate
  - yaml
  - document kinds
  - exit codes
  - errors
cliCommand: validate
related:
  - cli-run
  - cli-plan
  - flow-overview
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Core/Secrets/SecretHygiene.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
---

# sqlflow validate

## Synopsis

```bash
sqlflow validate <pipeline.flow.yaml> [-v|--verbose]
```

## Description

Parses and validates one flow document without touching a database and without executing anything. The file is loaded through the same shared load path that `sqlflow run` uses (`DocumentLoader.Load` in src/SqlFlow.Execution/DocumentLoader.cs), so a document that validates cleanly is the exact document a run would execute: the same YAML mapping, the same file-relative path fixups, and the same secret-hygiene check.

On success the command prints one `OK  ...` line to stdout describing what the document declares and exits 0. On any load or validation failure it prints one `ERROR  ...` line to stderr (with credential values redacted) and exits 1.

Connection values such as `${env:NAME}` or `${keyvault:vault/secret}` are kept as literal references during validation; they resolve at run time. Validation therefore does not require the referenced environment variables or vault secrets to exist.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<pipeline.flow.yaml>` | yes | Path to the flow document to validate. Any of the eight document kinds is accepted (see below). Invoking `validate` without a file prints the usage text and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-v`, `--verbose` | flag | off | Prints diagnostics to stderr, including which git-ignored `.sqlflow/env` file was applied and how many variables it contributed. |
| `-h`, `--help` | flag | off | Prints the usage text. Exits 0 when a file argument is also present, 1 otherwise. |

## Behavior and output notes

### Document kind dispatch

The loader sniffs the document's root `flowType` key (compared case-insensitively) and dispatches to the matching kind. A document with no `flowType` key is a file flow. All eight kinds validate through this one command:

| Kind | Discriminator | OK line shape |
| --- | --- | --- |
| File flow | no `flowType` key | `OK  '<name>' is valid (source: <type>, target: [<schema>].[<table>]).` |
| Ingestion | `flowType: ing` | `OK  '<name>' is valid (ingestion: [<db>].[<schema>].[<table>] -> [<db>].[<schema>].[<table>]).` |
| Export | `flowType: exp` | `OK  '<name>' is valid (export: <source object> -> <trgPath> as <trgFiletype>).` |
| Stored procedure | `flowType: sp` | `OK  '<name>' is valid (stored procedure: EXEC <procedure> on '<server>').` |
| Invoke | `flowType: inv` | `OK  '<invoke alias>' is valid (invoke: <adf\|aut> <pipeline or runbook name>).` |
| Health check | `flowType: hc` | `OK  '<name>' is valid (health check: <N> metric(s) [<metric names>] per <dateColumn> on <target>).` |
| Source control | `flowType: scm` | `OK  '<name>' is valid (source control: database on '<server>' -> <remote url or (local history only)> [<branch>]).` |
| Batch | `flowType: batch` | `OK  '<name>' is valid (batch: include <globs>; onError stop\|continue; maxParallel <N\|unbounded>).` |

Details of the shapes:

- Relational object names print in bracketed form (`[DW].[raw].[Orders]` for three-part ingestion objects, `[dbo].[Orders]` for a file flow target).
- The invoke type code is `adf` (Azure Data Factory pipeline) or `aut` (Azure Automation runbook).
- A source-control flow without a configured remote prints `(local history only)` in place of the remote URL.
- A batch with `maxParallel` unset or `0` prints `unbounded`; the `onError` mode prints lowercased (`stop`, the default, or `continue`).

An unknown `flowType` value fails with the loader's error listing every accepted value:

```text
ERROR  <file>: unknown flowType '<value>'. Use 'ing' for a table-to-table ingestion flow, 'exp' for a file export, 'sp' for a stored-procedure flow, 'inv' for an ADF/Automation trigger, 'hc' for an ML health check, 'scm' for a database source-control snapshot, 'batch' for an ordered multi-flow batch, or omit flowType for a file flow.
```

A document kind the CLI cannot dispatch fails with `ERROR  Unhandled document kind.`.

### File-relative path fixups

Loading applies the same fixups that `run` applies, so `validate` reports the paths a run would actually use:

- A file flow's `source.location`, when relative, resolves against the flow document's own directory (absolute locations and non-file sources pass through unchanged).
- An export flow's `trgPath`, when relative and not a URI (no `://`), resolves against the document's directory. The export OK line prints the resolved absolute path.

Ingestion and stored-procedure flows carry no file path and pass through unchanged.

### Environment file

Before the document loads, the CLI applies the nearest git-ignored `.sqlflow/env` file, searched from the flow document's directory upward; the process environment always wins. With `--verbose`, the applied file and variable count print to stderr. A malformed env file fails the command with an `ERROR` line and exit 1.

### Secret hygiene

Every load runs the embedded-credential check from src/SqlFlow.Core/Secrets/SecretHygiene.cs. A connection value that is not a `${...}` reference and not an `@alias`, and that contains any of the secret-bearing keywords `password=`, `pwd=`, `client secret=`, `clientsecret=`, `secret=`, `accesskey=`, `access key=`, or `sharedaccesskey=` (compared case-insensitively), produces a warning on stderr:

```text
WARN  <file>: connection '<alias>' embeds a credential in the document. Files under source control must carry references instead: use ${env:NAME}, ${keyvault:vault/secret}, or a bare '<alias>:' (which resolves ${env:SQLFLOW_CONN_<ALIAS>}); put local values in the git-ignored .sqlflow/env file. See docs/environment-variables.md.
```

The credential value itself is never echoed, and the warning does not change the exit code: the document still validates and the command still exits 0. The check covers the document-level `connections:` of ingestion, export, stored-procedure, health-check, and source-control documents, plus a file flow's `target.connection`.

### Error output and redaction

Every failure prints exactly one line to stderr in the form `ERROR  <message>` and exits 1. The message passes through credential redaction first: any value following a secret-bearing keyword (for example inside a quoted connection string in a parser error) collapses to `[redacted]`. Representative messages:

```text
ERROR  Pipeline file not found: '<path>'.
ERROR  <file>: invalid YAML - <parser message>
```

Per-kind validation errors (missing required keys, malformed values) surface the loader's message in the same `ERROR` form.

## Examples

Validate the quickstart CSV file flow (samples/quickstart/orders.flow.yaml):

```bash
sqlflow validate samples/quickstart/orders.flow.yaml
```

```text
OK  'orders' is valid (source: csv, target: [dbo].[Orders]).
```

Validate a table-to-table ingestion flow (samples/ingestion/orders-ingestion.flow.yaml, `flowType: ing`):

```bash
sqlflow validate samples/ingestion/orders-ingestion.flow.yaml
```

```text
OK  'orders-ingestion' is valid (ingestion: [AdventureWorks].[Sales].[Orders] -> [DW].[raw].[Orders]).
```

Validate a batch document (samples/seed/seed-batch.flow.yaml, `flowType: batch`); the default error mode and parallelism are echoed:

```bash
sqlflow validate samples/seed/seed-batch.flow.yaml
```

```text
OK  'seed-batch' is valid (batch: include orders-export.flow.yaml; onError stop; maxParallel unbounded).
```

A document that embeds a credential still validates, but warns on stderr. Given a file flow whose target is `connection: Server=.;Database=DW;User Id=sa;Password=hunter2`:

```bash
sqlflow validate leaky.flow.yaml
```

```text
WARN  leaky.flow.yaml: connection 'target' embeds a credential in the document. Files under source control must carry references instead: use ${env:NAME}, ${keyvault:vault/secret}, or a bare 'target:' (which resolves ${env:SQLFLOW_CONN_TARGET}); put local values in the git-ignored .sqlflow/env file. See docs/environment-variables.md.
OK  'leaky' is valid (source: csv, target: [dbo].[Orders]).
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | The document loaded and validated; one `OK` line printed to stdout. Secret-hygiene warnings do not affect the exit code. |
| 1 | No file argument was given (usage printed); the file does not exist; the YAML failed to parse; the document failed kind dispatch or per-kind validation; or the `.sqlflow/env` file failed to apply. One redacted `ERROR` line prints to stderr. |

## Validating a whole estate

`validate` also accepts a folder: every `*.yaml` / `*.yml` under it (the `.sqlflow` work area excluded) is
validated through the exact same loader, one line per document, and the exit code is 0 only when every
document parses. With `--json` (on a folder or a single file) stdout becomes a machine-readable report array
of `{file, ok, kind, name, error}`, so a CI step can gate on it structurally:

```
sqlflow validate ./flows            # OK/BROKEN line per document, exit 1 if any is broken
sqlflow validate ./flows --json     # the report array for CI
```

## See also

- [sqlflow run](./run.md): execute the validated document.
- [sqlflow plan](./plan.md): preview the SQL a file flow would run, without changing anything.
- [Flow document overview](../flow/overview.md): the eight document kinds and their shared structure.
- [CLI conventions](../concepts/cli-conventions.md): output streams, exit codes, secret references, and the `.sqlflow/env` file.
