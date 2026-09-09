---
id: cli-db
title: "sqlflow db: migrate, sync, status"
type: cli-command
summary: Bootstrap the shadow catalog schema (migrate), project the YAML estate, run history, and lineage into it (sync), and report migration drift (status).
keywords:
  - shadow catalog
  - migrate
  - sync
  - status
  - sqlflow_catalog_db
  - repo
  - write-back
  - ef migrations
cliCommand: "sqlflow db <migrate|sync|status> [path] [--db <conn-ref>] [--create] [--repo name] [--repo-url url] [--connect]"
related:
  - concept-shadow-catalog
  - concept-control-plane
  - cli-lineage
  - cli-run
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Catalog/CatalogDatabase.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogTransaction.cs
  - src/SqlFlow.Catalog/CatalogDbContext.cs
  - src/SqlFlow.Core/Secrets/SecretHygiene.cs
---

# sqlflow db

## Synopsis

```bash
sqlflow db migrate [--db <conn-ref>] [--create]
sqlflow db status  [--db <conn-ref>]
sqlflow db sync    [path] [--db <conn-ref>] [--repo <name>] [--repo-url <url>] [--connect]
```

## Description

`sqlflow db` operates the shadow catalog: an EF Core-managed read model of the git/YAML estate, the on-disk `run.json` history, and computed lineage, built for the GUI and the control plane. Git stays the source of truth; the sync is one-directional (files into database, never the reverse), so the catalog can always be rebuilt by re-syncing, and several repos can sync into one catalog for cross-repo queries.

The command takes a subcommand instead of a pipeline file:

- `migrate` upgrades an existing catalog to the current schema version by applying all pending EF Core migrations. It does NOT auto-create a missing database: without `--create` it calls `MigrateExistingAsync` and refuses a missing or non-catalog database, so a mistyped `--db` can never silently provision the wrong (possibly production) server. Pass `--create` to provision a new catalog (create the database, or initialise the catalog in an empty one).
- `status` reports applied versus pending migrations without changing anything.
- `sync` migrates first, then projects the estate under `[path]` into the catalog: each YAML flow becomes a pipeline row (kind, source/target servers, the secret-redacted YAML text, and the full definition as queryable JSON), each `run.json` becomes a run row with drill-down detail, and lineage objects, columns, edges, waves, and flow dependencies are computed and stored.

All catalog tables live in the SQL Server schema `catalog` (`CatalogDbContext.SchemaName`), with migration history tracked in `catalog.__CatalogMigrationsHistory`. This is the only place in the product that uses Entity Framework; the engine itself stays on direct ADO.NET.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `migrate`, `sync`, `status` (case-insensitive). Anything else prints `Usage: sqlflow db <migrate\|sync\|status> [path] [--db <conn-ref>]` and exits 1. |
| `[path]` | no | `sync` only: the estate folder to project. Defaults to the current directory. |

## Options

All flags take their value as the next argument (`--db "$REF"`, not `--db=REF`).

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--db` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | Reference to the catalog database connection string. This is a reference, never an embedded secret: it is resolved through the secret resolver (`${env:NAME}` and `${keyvault:vault/secret}` schemes), with local values supplied by the git-ignored `.sqlflow/env` file, searched from the current directory upward. |
| `--create` | switch | off | `migrate` only: provision a new catalog (create the database, or initialise the `catalog` schema in an empty database) before applying migrations. Without it, `migrate` upgrades an existing catalog only and refuses a missing or non-catalog database. |
| `--repo` | string | folder name of `[path]`, fallback `default` | `sync` only: the repo name the synced estate is attributed to. Pipelines, runs, and lineage edges are scoped to this repo inside the catalog. |
| `--repo-url` | string | (none) | `sync` only: records the git remote URL on the repo row. A later sync without it preserves the previously recorded URL. |
| `--connect` | switch | off | `sync` only: adds the derived lineage tier. Object metadata and module bodies are fetched from the live SQL Servers (`sys.objects`, `sys.sql_modules`), which links flows across repos through shared objects. The summary line ends with `(connected)`. |

If the `--db` value looks like a literal connection string carrying a credential keyword (`password=`, `pwd=`, `secret=`, and similar), the command still runs but warns on stderr:

```text
WARN  --db embeds a credential on the command line (it lands in shell history). Prefer the canonical ${env:SQLFLOW_CATALOG_DB}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, with local values in the git-ignored .sqlflow/env file.
```

## Behavior and output

### migrate

`db migrate` without `--create` calls `CatalogDatabase.MigrateExistingAsync`: it applies exactly the pending migrations to an existing catalog and refuses a missing or non-catalog database rather than creating one. Add `--create` to call `CatalogDatabase.MigrateAsync` instead, which on an empty server creates the database and the `catalog` schema before applying migrations. Each migration runs in its own transaction, so no migration is ever half-applied. The initial CREATE DATABASE itself is not transactional, but re-running `migrate` is always safe, so the command is idempotent (a no-op when already current). On success it prints one line:

```text
OK   catalog database current at '<last-migration>' (N migration(s) applied, 0 pending).
```

When no migrations have ever been applied, the migration name is printed as `(none)`.

For EF design-time tooling (`dotnet ef migrations add`), the factory in src/SqlFlow.Catalog/CatalogDatabase.cs reads `SQLFLOW_CATALOG_DB` and falls back to `Server=(localdb)\MSSQLLocalDB;Database=SqlFlowCatalog;Trusted_Connection=True;TrustServerCertificate=True`.

### status

`db status` compares the migrations this build knows against those recorded in the database and prints:

```text
catalog: N migration(s) applied, M pending.
  pending: <migration-name>
```

with one `pending:` line (indented two spaces) per pending migration. It exits 0 when the database is current and 2 when any migration is pending, so it works as a CI drift gate.

### sync

`db sync` always migrates the schema first, so a sync against a fresh server just works. The whole pass then runs in one serializable transaction wrapped in the EF execution strategy (`CatalogTransaction.InSerializableAsync`), with the change tracker cleared before each retry attempt; concurrent syncs of the same repo serialize instead of racing, and the estate is re-collected from disk on each attempt so a retry is safe.

The pass projects three tiers:

1. **Pipelines.** Every flow document under `[path]` is upserted by its stable identity (repo + flow name). Unchanged content (detected by hash) only re-affirms presence; flows that have left the estate are deactivated, keeping their run history; re-adding a flow reactivates it. YAML-declared schedules are mirrored into the schedule table, and authored per-column transforms are projected into declared pipeline-column rows.
2. **Runs.** Every `run.json` under a `.sqlflow/runs/` directory below `[path]` is read; runs are inserted once by their run id, so re-syncing the same folders (or aggregating many nodes' folders) is idempotent. Each new run also lands its drill-down detail: processed files, assertions, generated statements, surrogate keys, and health-check metrics. A `run.json` over 64 MiB, or one missing required fields, is skipped with a warning and counted as unreadable; a flow document over 16 MiB is likewise skipped.
3. **Lineage.** Declared (YAML) plus observed (run artifacts) lineage is computed offline; `--connect` adds the derived tier from the live databases.

Secrets never rest in the catalog: a flow document that appears to embed a credential produces a warning, and the stored YAML and definition JSON are passed through the same redactor used for connection-string error text. Secret references needed by `--connect` are resolved through the CLI's secret resolver (environment variables, the `.sqlflow/env` file applied at startup, and `${keyvault:...}`).

On success `sync` prints one summary line:

```text
OK   synced '<dir>': pipelines +A added, U updated, N unchanged, D deactivated; runs +R added (F files, X assertions, S statements, K surrogate-keys, H hc-metrics), Sk known, Fd unreadable; lineage O objects, C columns, E edges, W waves, Dep dependencies[, Z superseded keys healed][ (connected)].
```

The `superseded keys healed` fragment appears only when weaker-keyed twin object rows (the same object recorded without its database or schema qualifier) were removed because no lineage edge references them anymore (identity healing); `(connected)` appears only with `--connect`. At most the first 20 warnings are printed to stderr as `WARN  <text>`; warnings do not change the exit code.

### Run write-back (keeping the catalog current without manual syncs)

After any `sqlflow run`, when `--db` is passed or `SQLFLOW_CATALOG_DB` is set, the produced run(s) are automatically recorded into the catalog (the pipeline row plus the run and its drill-down detail); a batch records the batch document plus each member. Opt out with `--no-db-sync` on the run. Repo attribution follows `--repo`, then `SQLFLOW_REPO`, then the primary flow's folder name. Lineage and execution waves are NOT recomputed by write-back; that remains `db sync`'s job. Write-back is best-effort: a failure prints

```text
WARN  catalog write-back skipped (<redacted message>); the run itself is unaffected. Run 'sqlflow db sync' to backfill.
```

and never changes the run's own exit code.

The flows under samples/seed (orders-dw.flow.yaml, orders-export.flow.yaml, orders-hc.flow.yaml, seed-batch.flow.yaml) exist specifically to populate a catalog with genuine run history covering every drill-down detail kind.

## Examples

Bootstrap a catalog and check it, using the canonical environment variable:

```bash
export SQLFLOW_CATALOG_DB="Server=localhost;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True"

sqlflow db migrate
# OK   catalog database current at '20260702125200_PipelineTransformColumns' (15 migration(s) applied, 0 pending).

sqlflow db status
# catalog: 15 migration(s) applied, 0 pending.
```

Sync a repo's estate, run history, and derived lineage into the catalog (from samples/lineage-demo/README.md):

```bash
sqlflow db sync . --repo lineage-demo --connect
```

Sync a specific folder under an explicit repo name and remote, against an explicit connection reference:

```bash
sqlflow db sync ./flows --repo warehouse --repo-url https://github.com/acme/warehouse.git --db '${env:SQLFLOW_CATALOG_DB}'
```

Use `db status` as a CI drift gate (exit 2 means a deploy must run `db migrate` first):

```bash
if ! sqlflow db status; then
  echo "catalog schema is behind this build; run: sqlflow db migrate"
  exit 1
fi
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | `migrate` or `sync` succeeded (warnings on stderr do not affect it); `status` found no pending migrations. |
| 1 | Unknown subcommand (usage printed); the `--db` reference failed to resolve; any EF/SqlClient failure, reported as `ERROR  catalog '<sub>' failed: <redacted message>`. |
| 2 | `status` found pending migrations. |

Error messages are passed through the credential redactor, so a connection string quoted by a driver exception never leaks a password into logs.

## See also

- [Shadow catalog](../concepts/shadow-catalog.md)
- [Control plane](../concepts/control-plane.md)
- [sqlflow lineage](lineage.md)
- [sqlflow run](run.md)
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
