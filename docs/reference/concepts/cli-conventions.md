---
id: concept-cli-conventions
title: "CLI conventions: argument parsing and exit codes"
type: concept
summary: How the sqlflow CLI parses positionals and options, where value-taking flags may be placed, and what each exit code (0, 1, 2) means.
keywords:
  - argument parsing
  - positionals
  - options
  - exit codes
  - exit 2
  - verbose
  - help
related:
  - cli-run
  - cli-healthcheck
  - cli-db
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - tests/SqlFlow.Core.Tests/CliArgumentParsingTests.cs
---

# CLI conventions: argument parsing and exit codes

The `sqlflow` CLI uses a hand-rolled argument parser in src/SqlFlow.Cli/Program.cs rather than a framework. Every command shares one convention set: positionals name the command and its file or folder, options are dash-prefixed tokens read positionally-independently, and exit codes carry machine-readable meaning for CI gating. This page defines those conventions so tooling (scripts, CI pipelines, editors) can invoke the CLI and interpret its results reliably.

## Positionals and the command line shape

`Program.PositionalArguments` walks the raw argument array and collects every token that does not start with `-`. When it meets a token in `Program.ValueTakingOptions` (the explicit set of options that consume the next token) it also skips the following token, so an option's value never leaks into the positional list. This is guarded by tests/SqlFlow.Core.Tests/CliArgumentParsingTests.cs: `flatten folder --xml //a/b` yields the positionals `flatten folder`, while the boolean flag in `of --up value` does not swallow `value`.

The resulting positional list is interpreted as:

- `positional[0]` is the command, lowercased.
- `positional[1]` is the pipeline file or flow folder for every command except the no-file verbs, which supply their target through flags or a subcommand instead. `Program.Main`'s positional-count gate exempts `healthcheck`, `auth`, `db`, `worker`, `runs`, `user`, `detect-unique-key`, and every control-plane verb (`health`, `login`, `logout`, `trigger`, `groups`, `whoami`, `doctor`, `summary`, `nodes`, `schedules`, `repos`, `pipelines`, `datasources`, `search`, `completions`) from needing a second positional (src/SqlFlow.Cli/Program.cs). healthcheck addresses its table through `--source`/`--object`, auth is a pure environment check, db and catalog take a subcommand as their second positional, and the control-plane verbs address the remote API through flags.

If the required positionals are missing, the CLI prints usage and exits 1. `-h` or `--help` prints usage and exits 0, unless the arguments were also insufficient, in which case the exit code is still 1.

## Value-taking options

`Program.ValueTakingOptions` is the authoritative registry of options that consume the next token:

```text
-o --out --log-level
--max-files --max-records --max-depth
--source --target --database --schema --target-schema --provider
--like --offset --limit --term --object --target-object --keys --name
--pattern --root --keep --include --exclude --explode --aliases
--separator --join-separator --map --array --repeat --xml
--date-column --base-value --filter --threshold --alpha --budget --maturity --state-dir
--of --explain
--db --repo --repo-url
--url --token --username --token-name --expires-days --scopes
--scope --batch --pool --poll-seconds --commit --flow --status --kind --group
--page --page-size --from --to --file-pattern
--cron --interval --timezone --remote-url --credential-ref --credential-user
--ref --sample --max-columns --max-candidates --active --enabled
--search --relation --tier --server --operation --last
```

Options in this set can be placed anywhere on the command line; the positional extraction skips their values regardless of position.

**Placement.** The set is comprehensive: the run backfill parameters (`--from`, `--to`, `--file-pattern`), the auth `--scope`, the worker `--poll-seconds` and `--pool`, and the detect-unique-key `--sample`, `--max-columns`, and `--max-candidates` are all in `ValueTakingOptions`, so their values are consumed as option values rather than leaking into the positional list. There is no positional-ordering constraint on them: `sqlflow run --from 2023-01-15 pipelines/orders.yaml` and `sqlflow run pipelines/orders.yaml --from 2023-01-15` parse identically, because the parser skips `--from`'s value wherever the flag sits.

`worker` is a genuine no-file exemption: `Program.Main`'s positional-count gate exempts `worker` alongside `healthcheck`, `auth`, `db`, and the rest (src/SqlFlow.Cli/Program.cs), so a bare `sqlflow worker` (with only the default `--db`) runs the queue drain loop directly. It needs no second positional token and never prints usage or exits 1 for a missing one. `sqlflow worker --db '${env:SQLFLOW_CATALOG_DB}'` starts normally.

## Option value resolution

`GetOption` finds the first occurrence of any of the flag's spellings and returns the following token, with two rules:

- If the next token starts with `-` and is longer than one character, `GetOption` returns `null`. A value-taking flag with no value therefore cannot swallow the next flag (for example `--explode --data` does not set explode paths to `"--data"`).
- A lone `-` is still a valid value, for example as a `--separator`.

Numeric options are lenient by design:

- `ParseIntOption` returns the default when the value is missing, unparseable, or negative; it never errors.
- `ParseDoubleOption` requires a finite positive number (invariant culture) and otherwise returns the default.

`--log-level` is strict: anything other than `info`, `debug`, or `trace` throws `Unknown --log-level '<value>'. Allowed: info, debug, trace.`

## Overloaded flags

`--json` has two meanings depending on the command:

- For `run`, `catalog`, `healthcheck`, `lineage`, and `detect-unique-key` it is a boolean flag: emit the result as JSON on stdout.
- For `flatten` it is a value-taking option (it is mapped via `MapOption` to the `jsonPaths` source option, keeping JSON subtrees as string columns). The generic `--keep` flag sets the same option (`jsonPaths` for JSON, `xmlPaths` for XML) without the name collision.

`-o`/`--out` writes the primary output to a file instead of stdout for `infer` (inference report), `flatten` (formula or CSV), `healthcheck` (scored report), `lineage` (a copy of the report), `detect-unique-key` (unique-key report), and `catalog scaffold` (the generated flow file); `catalog scaffold-all` requires `--out <directory>` for its generated flow files.

## Verbosity and diagnostics

`-v`/`--verbose` sets the console logger minimum level to Debug (single-line output, `HH:mm:ss` timestamps, scopes included) and prints the `.sqlflow/env` notice on stderr when a local env file was applied: `env: applied <n> variable(s) from <path>`.

Any `SqlFlowException` escaping a command handler is caught at the top level and printed as `ERROR  <message>` on stderr with connection-string secrets redacted, exit 1. An unrecognized command prints `Unknown command '<cmd>'.` followed by usage, exit 1.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success. Every command uses 0 for a clean result. |
| 1 | Failure: a failed run, a usage error, or an escaped `SqlFlowException`. |
| 2 | A distinguishable signal, never an error (see below). |

Exit 2 is reserved for "the command worked, and the answer is the one you asked to be told about":

- `run` (health-check documents) and `healthcheck` return 2 when `--fail-on-anomaly` is set and the check found anomalies; a failed check still returns 1, so a broken run is distinguishable from a genuine anomaly in CI.
- `db status` returns 2 when catalog migrations are pending (0 when the schema is current).
- `lineage` returns 2 only when `--strict` is set and the graph contains cycles; without `--strict`, cycles do not affect the exit code.
- `detect-unique-key` returns 2 when no unique key was found (0 when at least one verified candidate is unique).

Command-specific notes:

- `validate` exits 0 for a valid document of any flow kind.
- `plan` exits 1 for non-file documents; only file flows can be planned offline.
- `auth` exits 0 only when an Azure token was actually acquired for the requested scope, 1 otherwise.
- Batch runs exit 0 only when the whole batch succeeded; per `BatchRunResult.Success`, a member failure listed in `ignoreErrors` does not by itself fail the batch.

## Configuration touchpoints

- CLI flags: `-v`/`--verbose`, `-h`/`--help`, `-o`/`--out`, `--json`, `--log-level`, `--fail-on-anomaly`, `--strict`.
- Environment: the git-ignored `.sqlflow/env` file (searched from the flow document's directory upward) supplies local values for `${env:...}` references before any command resolves; the process environment always wins. `SQLFLOW_CATALOG_DB` is the default `--db` reference for `db` and `worker`; `SQLFLOW_AZURE_AUTH` selects the auth mode `auth` reports.
- YAML: none. Argument parsing and exit codes are host behavior; flow documents do not configure them.

## Examples

Flag placement is free: `--from`/`--to`/`--file-pattern` are in `ValueTakingOptions`, so their values are skipped wherever the flag sits and never mistaken for the pipeline file. All three of these parse identically:

```bash
sqlflow run pipelines/orders.yaml --full
sqlflow run pipelines/orders.yaml --from 2023-01-15 --to "2023-02-01 06:00:00"

# Also correct: the flag before the file. '2023-01-15' is consumed as --from's value, not the positional.
sqlflow run --from 2023-01-15 pipelines/orders.yaml
```

CI gating on exit 2:

```bash
sqlflow healthcheck --object dw.fact.Sales --source '${env:SQLFLOW_SOURCE}' --fail-on-anomaly
case $? in
  0) echo "healthy" ;;
  2) echo "anomaly detected, gate the deploy" ;;
  *) echo "the check itself failed" ; exit 1 ;;
esac
```

The two meanings of `--json`:

```bash
# Boolean: JSON result on stdout.
sqlflow run pipelines/orders.yaml --json

# Value-taking (flatten only): keep these JSON subtrees as string columns.
sqlflow flatten data/events.json --json /payload/raw
```

## See also

- [run](../cli/run.md)
- [healthcheck](../cli/healthcheck.md)
- [db](../cli/db.md)
