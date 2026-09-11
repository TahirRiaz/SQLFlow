---
id: concept-environment-variables
title: SQLFLOW_* environment variables and the .sqlflow/env file
type: concept
summary: Every environment variable the CLI, workers, and control plane read, plus the git-ignored .sqlflow/env file and its precedence rules.
keywords:
  - sqlflow_conn
  - sqlflow_catalog_db
  - sqlflow_source
  - env file
  - .sqlflow/env
  - precedence
  - sqlflow_azure_auth
  - secrets
related:
  - concept-connections-and-secrets
  - flow-connections
  - cli-db
sourceRefs:
  - src/SqlFlow.Core/Connections/ConnectionConvention.cs
  - src/SqlFlow.Core/Secrets/LocalEnvFile.cs
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Azure/AzureAuth.cs
  - src/SqlFlow.Azure/AzureCredentialFactory.cs
  - src/SqlFlow.Azure/AzureEnvironment.cs
  - src/SqlFlow.Catalog/CatalogDatabase.cs
  - src/SqlFlow.Node/GitMaterializer.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - Dockerfile.worker
  - deploy/docker/worker-entrypoint.sh
  - deploy/compose/docker-compose.yml
---

# SQLFLOW_* environment variables and the .sqlflow/env file

Flow documents live under source control; credentials never do. Documents carry connection references (`${env:NAME}`, `${keyvault:vault/secret}`, or a bare alias), and the values arrive through the process environment. In production and CI the orchestrator injects them; on a developer machine they live in the git-ignored `.sqlflow/env` file. This page lists every variable the tooling reads and the exact precedence rules.

All conventional names are strictly UPPERCASE because environment variables are case-sensitive on Linux; one spelling works everywhere.

## The SQLFLOW_* variable family

| Variable | Read by | Meaning |
|---|---|---|
| `SQLFLOW_CONN_<NAME>` | secret resolver | The value of a connection declared as a bare alias `<NAME>` in a document's `connections:` block. |
| `SQLFLOW_SOURCE` | `healthcheck`, `detect-unique-key`, `catalog scaffold` | The conventional default source connection. |
| `SQLFLOW_DW` | `catalog scaffold` | The conventional explicit target reference; the scaffold embeds `${env:SQLFLOW_DW}` as the target placeholder. |
| `SQLFLOW_CATALOG_DB` | `db`, run write-back, control plane, EF tooling | The shadow-catalog database connection. A `worker` never reads it: a node needs only the control plane. |
| `SQLFLOW_REPO` | post-run catalog write-back | Repo attribution for recorded runs when `--repo` is not passed. |
| `SQLFLOW_GIT_TOKEN` | workers, control plane | Token for cloning private git remotes during SHA-pinned materialization. Unset means anonymous access (public or local-path remotes). |
| `SQLFLOW_GIT_USERNAME` | workers, control plane | Optional username paired with `SQLFLOW_GIT_TOKEN`; when blank, GitHub's conventional placeholder `x-access-token` is used. |
| `SQLFLOW_AZURE_AUTH` | every Azure access path | Selects the Azure credential mode (see below). |
| `SQLFLOW_TEST_DB` | this repository's integration tests | The integration-test sink database; the legacy name `SQLFlowSinkConStr` is honored as a fallback in the tests. |
| `SQLFLOW_WORKER_POOL` | `Dockerfile.worker` entrypoint only | Comma-separated pools the container serves; translated to `--pool`. |
| `SQLFLOW_WORKER_POLL_SECONDS` | `Dockerfile.worker` entrypoint only | How long each poll waits for work; translated to `--poll-seconds`. |
| `SQLFLOW_URL` | `worker` and every control-plane verb | The control plane base URL (`--url`). A worker polls it for work. |
| `SQLFLOW_TOKEN` | `worker` and every control-plane verb | The bearer credential (`--token`); a worker's must carry the `node` scope. |

### SQLFLOW_CONN_&lt;NAME&gt;: the bare-alias convention

The single mapping rule lives in `ConnectionConvention.EnvironmentVariable` (src/SqlFlow.Core/Connections/ConnectionConvention.cs): the alias is trimmed, uppercased, and every non-alphanumeric character is folded to `_`, then prefixed with `SQLFLOW_CONN_`. So `my-dwh` resolves `SQLFLOW_CONN_MY_DWH`, and the resolvable reference form is `${env:SQLFLOW_CONN_MY_DWH}`. The YAML loaders apply this rule to every bare alias in a `connections:` block; nothing else restates it.

```yaml
connections:
  dwh:            # bare alias: resolves ${env:SQLFLOW_CONN_DWH}
```

### SQLFLOW_SOURCE and SQLFLOW_DW

`sqlflow healthcheck` and `sqlflow detect-unique-key` both default `--source` to the reference `${env:SQLFLOW_SOURCE}` when the flag is omitted; the resolver's error names the variable when it is not set. `sqlflow catalog scaffold` applies the no-secret embedding rule to generated files: a `--source` or `--target` value that is a whole `${...}` reference is embedded verbatim, and anything else (a literal connection string) becomes the placeholder `${env:SQLFLOW_SOURCE}` (source) or `${env:SQLFLOW_DW}` (target), so a secret can never leak into a generated file.

### SQLFLOW_CATALOG_DB

Four consumers, one variable (and one deliberate non-consumer: `sqlflow worker` opens no catalog connection, so a compute node's environment never holds it):

- `sqlflow db migrate|sync|status` and `sqlflow runs cancel` default `--db` to the reference `${env:SQLFLOW_CATALOG_DB}`.
- The automatic post-run catalog write-back activates only when `--db` is passed or `SQLFLOW_CATALOG_DB` is set; otherwise the run is a pure file/YAML operation and the catalog is simply absent (no error, no output). `--no-db-sync` disables the write-back even when the variable is set. A write-back failure is a warning only; the run's own outcome is never affected, and `sqlflow db sync` backfills later.
- The control plane's catalog connection (`ControlPlane:Catalog:ConnectionReference`) defaults to `${env:SQLFLOW_CATALOG_DB}`.
- The EF design-time factory (`CatalogDbContextFactory` in src/SqlFlow.Catalog/CatalogDatabase.cs) reads it when `dotnet ef` tooling needs a real database; when unset it falls back to a LocalDB connection string.

Passing a raw connection string as `--db` triggers the secret-hygiene warning: prefer `${env:SQLFLOW_CATALOG_DB}` or another `${env:NAME}` / `${keyvault:vault/secret}` reference, with local values in `.sqlflow/env`.

### SQLFLOW_REPO

Repo attribution for the post-run catalog write-back. Precedence: `--repo`, then `SQLFLOW_REPO` (when non-empty), then the flow file's folder name, then the literal `default`.

### SQLFLOW_TEST_DB and SQLFlowSinkConStr

`SQLFLOW_TEST_DB` is the canonical name for this repository's own integration-test sink; the legacy `SQLFlowSinkConStr` is honored as a fallback, and when only the canonical name is set the tests bridge the legacy name into the process environment so `${env:SQLFlowSinkConStr}` references inside test YAML keep resolving. When neither is set (or the sink is unreachable) the integration tests skip. Separately, `sqlflow flatten` writes `${env:SQLFlowSinkConStr}` as the generated formula's `target.connection`, with a comment telling you to set the target before running.

## Azure authentication variables

`SQLFLOW_AZURE_AUTH` selects one credential intent for every Azure access path (Key Vault, ADF/Automation invoke, DuckDB object storage). The value is trimmed and lowercased:

| Value | Mode |
|---|---|
| `serviceprincipal`, `sp` | Client-secret service principal. Requires `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`; a missing one fails with `Service-principal auth requires environment variable '<name>'.` |
| `managedidentity`, `mi`, `msi` | Managed identity. A non-empty `AZURE_CLIENT_ID` selects a user-assigned identity; otherwise system-assigned. |
| `azurecli`, `cli`, `azlogin` | The signed-in Azure CLI user. |
| anything else (including unset) | The default credential chain. |

Caveat: DuckDB cloud reads support only a system-assigned managed identity. Combining `SQLFLOW_AZURE_AUTH=mi` with a non-empty `AZURE_CLIENT_ID` on a DuckDB source fails loudly; the error suggests `SQLFLOW_AZURE_AUTH=sp` instead. The .NET paths (Key Vault, invoke) do honor `AZURE_CLIENT_ID`.

`IS_RUNNING_IN_AZURE=true|false` force-overrides in-Azure detection in both directions. Without the override, the process counts as in-Azure when any of these is non-empty: `WEBSITE_INSTANCE_ID`, `FUNCTIONS_WORKER_RUNTIME`, `AZURE_CONTAINER_INSTANCE_ROOT_PATH`, `KUBERNETES_SERVICE_HOST`, `AZURE_VM_RESOURCE_GROUP`, `MSI_ENDPOINT`, `MSI_SECRET`, `IDENTITY_HEADER`, `APPSETTING_WEBSITE_SITE_NAME`. Off-cloud, the default chain excludes the managed-identity credential (no stall on the IMDS probe) and enables developer credentials (Visual Studio, VS Code, interactive browser, Azure CLI).

## Container and compose contracts

The worker container (`Dockerfile.worker`) is configured through environment only; `deploy/docker/worker-entrypoint.sh` composes the `sqlflow worker` invocation:

- `SQLFLOW_URL` (required): the control plane the node polls for work; the CLI default picks it up so it never appears on the command line.
- `SQLFLOW_TOKEN` (required): a personal access token with the `node` scope, or a `${env:...}`/`${keyvault:...}` reference to one. Neither the URL nor the token is put on the command line, so nothing sensitive appears in `ps` output. No catalog connection is needed: the run's definition, its snapshotted YAML, its lineage context and its live trace all travel over the node protocol.
- `SQLFLOW_WORKER_POOL` (optional): comma-separated pools, passed as `--pool`. Empty means the node takes untargeted runs only.
- `SQLFLOW_WORKER_POLL_SECONDS` (optional): passed as `--poll-seconds`; the CLI default is 30.
- `SQLFLOW_GIT_TOKEN` (optional): private-remote materialization.
- Plus every `${env:...}` reference the flows themselves use (source and target connection strings).

The control plane container takes ASP.NET configuration environment names, as `deploy/compose/docker-compose.yml` shows: `ControlPlane__Catalog__ConnectionReference`, `ControlPlane__Jwt__SigningKey`, `ControlPlane__Jwt__BootstrapSecret`, `ControlPlane__Bootstrap__AdminUsername`, `ControlPlane__Bootstrap__AdminPasswordReference`, `ControlPlane__Cors__AllowedOrigins__0`, `ControlPlane__Worker__Enabled`.

### Feature gates

Two control-plane features are OFF unless a deployment turns them on. Both are gated by a single boolean, and with the gate off the feature's endpoints answer with a clear "not enabled" problem rather than failing obscurely, so a GUI or an assistant can explain instead of erroring.

| Variable | Default | Gates |
| --- | --- | --- |
| `ControlPlane__Assistant__Enabled` | `false` | The GUI chat assistant (`/api/v1/chat`). Needs the provider settings alongside it; `GET /api/v1/chat/capabilities` reports the switch ([Chat assistant](../guides/chat-assistant.md)) |
| `ControlPlane__DataOps__Enabled` | `false` | The data-operations surface: ad-hoc business queries (prepare/run), the duplicate-key check (`duplicateKeys`), and the old-versus-new baseline comparison (`compareBaseline`). `GET /api/v1/dataops/capabilities` reports the switch ([Data operations](data-operations.md)) |

`ControlPlane__DataOps__Enabled` is the one to set when the assistant reports it can only reach metadata and cannot run a query, or when someone asks for the duplicate-key check or a migration comparison against old production, and gets a 403 naming the setting:

```bash
ControlPlane__DataOps__Enabled=true

# The baseline comparison additionally needs its linked servers allowlisted. A linked-server name becomes
# an identifier in generated SQL and a route into another estate, so it is configuration, never something
# a request chooses; an unlisted name is refused. Indexed keys bind an array, as with CORS origins.
ControlPlane__DataOps__Comparison__LinkedServers__0=old-dwh-prod
ControlPlane__DataOps__Comparison__LinkedServers__1=old-pre-prod
ControlPlane__DataOps__Comparison__LinkedServers__2=old-sqlflow-prod
ControlPlane__DataOps__Comparison__LinkedServers__3=OLDPROD
ControlPlane__DataOps__Comparison__DefaultLinkedServer=old-dwh-prod

# Optional: narrow further to named databases on those servers. Empty (the default) permits any database
# the linked server's own login can reach, which is usually right because the linked server IS the boundary.
ControlPlane__DataOps__Comparison__Databases__0=dw-dwh-prod
```

Turning the gate off again is a complete kill switch: the operations are refused at the trust boundary, so nothing is queued and no node ever opens a connection for them. Everything behind the gate is read-only in any case (the duplicate check groups and counts; the comparison reads both estates and writes nothing but a session temp table), so the gate is about limiting the surface a deployment exposes, not about preventing writes.

`DefaultLinkedServer` must be one of `LinkedServers` or the host fails at startup with a message naming the setting, so a typo is caught on deploy rather than on first use.

## The .sqlflow/env file

`LocalEnvFile.RelativePath` fixes the canonical local-development secrets file at `.sqlflow/env` (src/SqlFlow.Core/Secrets/LocalEnvFile.cs). Before any command runs, the CLI searches for the nearest one and applies it to the process environment:

- The search anchors at the pipeline file's directory when the positional is a file, at the folder itself when the positional is a directory (lineage), and at the current directory otherwise, then walks upward through parents. The first file found wins; no merging across levels.
- The process environment ALWAYS outranks the file: a variable that is already set is never overwritten, so CI, Kubernetes, and scheduler variables can never be shadowed by a stray local file.
- With `--verbose` the CLI prints `env: applied N variable(s) from <file>` to stderr. Values are never logged anywhere; the loader reports only names.
- The whole `.sqlflow/` folder (this file, run artifacts, model state, lineage output) belongs in `.gitignore`.

### Format

`KEY=VALUE` lines, `#` comments, blank lines. Values may be wrapped in single or double quotes to preserve leading/trailing spaces or a `#`. Variable names allow only ASCII letters, digits, and `_`.

```text
# .sqlflow/env - local development values. Never committed: .sqlflow/ is git-ignored.
SQLFLOW_CONN_DWH=Server=localhost,1433;Database=DW;User ID=dev;Password=...;TrustServerCertificate=True
SQLFLOW_SOURCE="Server=localhost,1433;Database=Staging;Integrated Security=True"
```

Malformed lines fail loudly, and the whole command aborts (a half-loaded secrets file is a debugging trap):

- A line without a valid `KEY=` prefix: `<path>(<line>): expected KEY=VALUE (or a # comment).` The line content is deliberately never echoed, because a malformed line in a secrets file may BE the secret, pasted without its `KEY=`.
- An invalid name: `<path>(<line>): '<name>' is not a valid environment variable name (letters, digits, '_').`

## Precedence, summarized

For any `${env:NAME}` reference or bare alias, the value comes from, in order:

1. The process environment (CI secret injection, Kubernetes `envFrom`, the scheduler, your shell).
2. The nearest `.sqlflow/env` file, applied only for names the process environment does not already set.

There is no third layer; if neither supplies the name, resolution fails with an error naming the variable.

## Example

A flow document that carries only references, with local values supplied by `.sqlflow/env`:

```yaml
connections:
  shop:
    provider: mysql
    connection: ${env:SHOP_MYSQL}
  dwh: ${env:SQLFLOW_DW}
```

```bash
# Local development: values come from the nearest .sqlflow/env; --verbose confirms the load.
sqlflow run flows/orders-ingestion.flow.yaml --verbose

# Zero-flag commands against the usual estate: SQLFLOW_SOURCE supplies the connection.
sqlflow healthcheck --object dbo.Orders
sqlflow detect-unique-key --object DW.dbo.Orders

# Catalog-backed run: SQLFLOW_CATALOG_DB triggers the automatic post-run write-back,
# attributed to SQLFLOW_REPO (or the flow folder's name when unset).
sqlflow run flows/orders-ingestion.flow.yaml --repo analytics
```

## See also

- [Data operations](data-operations.md)
- [Connections and secrets](connections-and-secrets.md)
- [The connections block](../flow/connections.md)
- [sqlflow db](../cli/db.md)
