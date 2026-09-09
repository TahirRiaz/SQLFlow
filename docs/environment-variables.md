# Environment Variables and Secrets: the Canonical Contract

> This document predates the verified reference corpus and has known drift: it omits several
> accepted `SQLFLOW_AZURE_AUTH` value aliases (`sp`, `mi`, `msi`, `azurecli`, `azlogin`; see
> [AzureAuth.cs](../src/SqlFlow.Azure/AzureAuth.cs)), and it does not mention that
> `detect-unique-key` also defaults `--source` from `SQLFLOW_SOURCE`. For the current, code-verified
> contract see [docs/reference/concepts/environment-variables.md](reference/concepts/environment-variables.md)
> and [docs/reference/concepts/connections-and-secrets.md](reference/concepts/connections-and-secrets.md).

Flow documents live under source control. Credentials never do. This page is the one contract that makes
those two facts compatible: which names exist, where values come from on each platform, and what the tool
does to keep secrets out of your repository. Everything here works identically on Windows, Linux, macOS, and
containers; SQLFlow is plain .NET and takes no platform-specific dependencies for any of it.

## The rule

A flow document carries connection REFERENCES, never values. Three reference forms exist, in order of
preference:

| Form | Example | Use |
|---|---|---|
| Bare alias (the convention) | `dwh:` | Resolves `${env:SQLFLOW_CONN_DWH}`. The document names WHAT it needs; operations decide the value. |
| Environment reference | `${env:SQLFLOW_DW}` | Explicit variable name, when the convention does not fit. |
| Key Vault reference | `${keyvault:my-vault/dw-conn}` | The value itself lives in Azure Key Vault; SQLFlow fetches it with its Azure identity. |

A literal connection string in a document is acceptable only when it carries no secret (Integrated Security,
Active Directory Default). `sqlflow validate` and `sqlflow run` print a loud warning whenever a document
embeds a `Password=`-style literal, naming the alternatives. The warning never echoes the value.

## The canonical variable family

All names are UPPERCASE: environment variables are case-sensitive on Linux, and one spelling everywhere is
the only convention that survives a mixed estate.

| Variable | Meaning |
|---|---|
| `SQLFLOW_CONN_<NAME>` | The connection declared as `<NAME>` in a flow document's `connections:` block, when declared bare. `<NAME>` is uppercased and every non-alphanumeric character folds to `_` (`my-dwh` reads `SQLFLOW_CONN_MY_DWH`). |
| `SQLFLOW_DW` | The data warehouse: the conventional explicit reference for the primary SQL Server target. |
| `SQLFLOW_SOURCE` | The conventional default source: `sqlflow healthcheck` uses it when `--source` is omitted, and `catalog scaffold` embeds it as the placeholder. |
| `SQLFLOW_TEST_DB` | The integration-test sink database of this repository's own test suite (the legacy `SQLFlowSinkConStr` name is still honored). |
| `SQLFLOW_AZURE_AUTH` | How SQLFlow authenticates to Azure (`default`, `cli`, `managedidentity`, `serviceprincipal`); the standard `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_CLIENT_SECRET` family applies, exactly as the Azure SDK defines it. |

Inline connection strings in YAML remain SUPPORTED (quickstarts, throwaway local work, passwordless
strings), but they are not the canonical way: anything secret-bearing triggers the hygiene warning.

The bare-alias convention is the canonical form for enterprise documents:

```yaml
flowType: hc
name: orders-watch
connections:
  dwh:                  # resolves ${env:SQLFLOW_CONN_DWH}; nothing sensitive in this file, ever
target:
  server: dwh
  object: DW.dbo.Orders
dateColumn: OrderDate
baseValue: COUNT(*)
```

A non-SQL-Server source keeps its provider and takes the same convention:

```yaml
connections:
  shop:
    provider: mysql     # resolves ${env:SQLFLOW_CONN_SHOP}
```

## Where the values come from

### Production and CI: the process environment

Inject the variables with whatever already runs your workloads. The process environment always wins over
every other supply channel.

GitHub Actions:

```yaml
- run: sqlflow run flows/orders-watch.flow.yaml --fail-on-anomaly
  env:
    SQLFLOW_CONN_DWH: ${{ secrets.SQLFLOW_CONN_DWH }}
```

Kubernetes:

```yaml
envFrom:
  - secretRef:
      name: sqlflow-connections   # keys: SQLFLOW_CONN_DWH, SQLFLOW_CONN_SHOP, ...
```

Azure DevOps: a variable group backed by Key Vault, mapped with `env:` on the task. Linux cron and Windows
Task Scheduler: set the variables in the unit/task definition, not in a profile script.

### Local development: the `.sqlflow/env` file

Developers need real values without polluting shell profiles or, worse, the YAML. Put them in
`.sqlflow/env` next to the flow documents (any parent directory also works; the nearest file wins):

```
# .sqlflow/env - local development values. NEVER COMMITTED: .sqlflow/ is git-ignored.
SQLFLOW_CONN_DWH=Server=localhost,1433;Database=DW;User ID=dev;Password=...;TrustServerCertificate=True
SQLFLOW_SOURCE=Server=localhost,1433;Database=Staging;Integrated Security=True
```

Rules the loader enforces:

- `KEY=VALUE` lines, `#` comments, blank lines. Optional surrounding quotes for values with spaces or `#`.
- Variable names: letters, digits, `_` only. Malformed lines fail loudly with their line number, and the
  line's content is never echoed (a malformed line in a secrets file may BE the secret).
- The process environment wins: a CI variable can never be shadowed by a stray local file.
- Values are never logged; with `--verbose` the CLI reports only how many names were applied and from where.

The repository's `.gitignore` excludes the whole `.sqlflow/` folder (run artifacts, model state, and this
file). Keep that rule in every repository that holds flow documents.

### Cloud indirection: `${keyvault:vault/secret}`

When values must not exist on the machine at all, reference Azure Key Vault directly in the document. SQLFlow
authenticates with its ambient Azure identity (Managed Identity, Azure CLI, Service Principal); the secret
travels straight from the vault into the connection pool.

## Defense in depth, summarized

| Layer | Mechanism |
|---|---|
| Documents | References only; bare-alias convention; hygiene warning on validate AND run when a credential literal sneaks in |
| Local machines | Git-ignored `.sqlflow/env`; loader never echoes values; environment wins over file |
| Pipelines | Plain environment injection; no SQLFlow-specific secret plumbing to audit |
| Cloud | Key Vault references resolved by identity, not by stored credentials |
| Logs and artifacts | Connection strings are redacted before they reach any log, trace, error message, or run artifact |
| Rotation | Rotate at the injection point (vault, CI secret store); documents and state folders need no change |

## Naming new variables

Anything SQLFlow-conventional starts with `SQLFLOW_`. Connection values follow `SQLFLOW_CONN_<NAME>`; do not
invent parallel families per project. A variable that does not hold a secret (a path, a flag) follows the
same prefix for discoverability but does not belong in `.sqlflow/env` unless it varies per developer.

## Platform notes

SQLFlow is plain .NET with no Windows-only APIs anywhere in the source tree (audited: no registry, no
P/Invoke, no special folders, no default-encoding assumptions). The points worth knowing on a mixed estate:

- Environment variable names are case-sensitive on Linux and macOS; the canonical family is strictly
  UPPERCASE so one spelling works everywhere.
- `.sqlflow/env` parsing is identical on every OS (UTF-8, LF or CRLF, BOM tolerated).
- File paths in documents accept either separator; exported files are addressed with forward slashes, which
  every OS and every cloud URL accepts. Locations containing `://` are treated as cloud addresses end to
  end: they bypass local path resolution entirely and route to whichever store claims them, so a cloud
  storage provider plugs in beside the local one without touching path logic.
- File glob patterns follow the OS filesystem's case rules (case-sensitive on Linux): name your files and
  patterns consistently.
- The health check's AutoML sweeps both managed trainers (FastTree family) and native ones (LightGBM, which
  ships win-x64, linux-x64, and osx-x64 binaries). On a platform where a native trainer cannot load, that
  trial fails, is logged at debug level, and the experiment continues with the managed trainers: the check
  still completes with the best available model.
