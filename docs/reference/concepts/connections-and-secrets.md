---
id: concept-connections-and-secrets
title: Connection references, secret resolution, and hygiene
type: concept
summary: How SQLFlow classifies @alias, ${scheme:locator}, and inline connection references, resolves secrets, and warns on embedded credentials.
keywords:
  - "@alias"
  - "${env:}"
  - "${keyvault:}"
  - secret providers
  - redaction
  - hygiene warnings
  - secretless
  - connection reference
related:
  - flow-connections
  - concept-environment-variables
  - cli-auth
  - flow-target
sourceRefs:
  - src/SqlFlow.Core/Connections/ConnectionRef.cs
  - src/SqlFlow.Core/Connections/ConnectionResolver.cs
  - src/SqlFlow.Core/Connections/ConnectionConvention.cs
  - src/SqlFlow.Core/Connections/CredentialProfile.cs
  - src/SqlFlow.Core/Connections/ResolvedConnection.cs
  - src/SqlFlow.Core/Connections/DataSource.cs
  - src/SqlFlow.Core/Connections/DataSourceCapabilities.cs
  - src/SqlFlow.Core/Connections/NullDataSourceStore.cs
  - src/SqlFlow.Core/Connections/InMemoryDataSourceStore.cs
  - src/SqlFlow.Core/Connections/SourceProviders.cs
  - src/SqlFlow.Core/Secrets/SecretResolver.cs
  - src/SqlFlow.Core/Secrets/EnvSecretProvider.cs
  - src/SqlFlow.Core/Secrets/SecretHygiene.cs
  - src/SqlFlow.Core/Secrets/LocalEnvFile.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Azure/AzureAuth.cs
  - src/SqlFlow.Azure/AzureCredentialFactory.cs
  - src/SqlFlow.Azure/AzureKeyVaultSecretProvider.cs
  - src/SqlFlow.Azure/AzureKeyVaultSecretVault.cs
  - src/SqlFlow.SqlServer/WithoutDatabaseResolver.cs
  - src/SqlFlow.SqlServer/FullMode/SqlDataSourceStore.cs
  - src/SqlFlow.Execution/SqlFlowEngineServices.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Yaml/YamlSourceControlFlowLoader.cs
  - src/SqlFlow.Cli/Program.cs
---

# Connection references, secret resolution, and hygiene

Flow documents live under source control, so they carry connection REFERENCES, never credential values. A relational document (`flowType: ing`, `exp`, `sp`, `hc`, or `scm`: every kind with a `connections:` block) resolves its connections at runtime through one pipeline (`ConnectionResolver` in src/SqlFlow.Core/Connections/ConnectionResolver.cs): classify the reference, resolve an alias through the registry, expand `${...}` secret references, then canonicalize for the resolved provider kind. A file flow (no `flowType` key: a CSV/JSON/XML/Parquet/XLS source loaded into one SQL Server table) carries no `connections:` block and has no alias form; its `target.connection` is resolved directly by `ISecretResolver` (src/SqlFlow.Core/Secrets/SecretResolver.cs, called from src/SqlFlow.Core/Engine/FlowRunner.cs), which expands a `${...}` reference and passes a literal connection string through unchanged. A separate hygiene layer (src/SqlFlow.Core/Secrets/SecretHygiene.cs) warns when a literal credential does sneak into a document or a CLI flag, and redacts secret-bearing fragments from every error message and log line the CLI prints.

## The three reference shapes

A connection value is exactly one of three shapes; the `@` sigil is the only disambiguator (`ConnectionRef.Parse` in src/SqlFlow.Core/Connections/ConnectionRef.cs):

| Shape | Example | Meaning |
|---|---|---|
| `@alias` | `@dwh-prod` | A registry alias resolved through `IDataSourceStore`. Requires full mode (a configured control database). |
| `${scheme:locator}` | `${env:SQLFLOW_DW}` | A secret reference expanded by the registered secret providers. |
| Inline string | `Server=.;Database=DW;Integrated Security=true;` | A literal connection string. Must be self-authenticating (no resting password). |

An alias is `@` followed by letters, digits, `_`, `.`, or `-`. Anything else after `@` throws:

```text
Invalid connection alias '@...'. An alias is '@' followed by letters, digits, '_', '.', or '-'.
```

An empty reference throws `A connection reference must not be empty.` Everything that does not start with `@` is classified as inline; `${...}` references inside it are expanded later by the resolver, so a whole-string reference and an embedded reference both parse as `Inline`.

### Bare aliases in the connections block

In a document's `connections:` block, a name with no value at all (or a blank value) resolves the canonical environment variable by convention (`YamlDocumentParts.ParseConnectionNode` in src/SqlFlow.Yaml/YamlDocumentParts.cs):

```yaml
connections:
  dwh:            # resolves ${env:SQLFLOW_CONN_DWH}
  my-dwh:         # resolves ${env:SQLFLOW_CONN_MY_DWH}
```

The mapping is defined once in src/SqlFlow.Core/Connections/ConnectionConvention.cs: `SQLFLOW_CONN_<NAME>`, where the name is trimmed, uppercased, and every non-alphanumeric character is folded to `_`. Names are strictly uppercase because environment variables are case-sensitive on Linux.

A connections-block entry can also be a plain string (defaults to SQL Server), or a map with `provider` and `connection` keys. The map form without a `connection:` keeps the provider and takes the bare-alias convention. Allowed provider tokens: `mssql`, `sqlserver`, `azdb`, `mysql`, `postgres`, `postgresql`, `oracle`; an unknown token throws `'connections.<name>.provider' has unknown provider '...'. Allowed: mssql, azdb, mysql, postgres, oracle.`

## Secret resolution: `${scheme:locator}`

`SecretResolver` (src/SqlFlow.Core/Secrets/SecretResolver.cs) expands every occurrence of the pattern

```text
\$\{(?<scheme>[a-zA-Z]+):(?<locator>[^}]+)\}
```

anywhere inside a value, including embedded inside a longer connection string (for example `Server=srv;Password=${keyvault:my-vault/dw-pw};`). A literal with no references is returned unchanged. Schemes are matched case-insensitively against the registered `ISecretProvider`s; an unknown scheme throws:

```text
No secret provider is registered for scheme '<scheme>' (referenced by '<value>').
```

Both a synchronous `Resolve` and an asynchronous `ResolveAsync` exist; the Key Vault async path is genuinely asynchronous.

### Registered providers

`AddSqlFlowEngine` (src/SqlFlow.Execution/SqlFlowEngineServices.cs) registers exactly two providers, plus the `AzureCredentialFactory` they share:

| Scheme | Provider | Behavior |
|---|---|---|
| `env` | `EnvSecretProvider` | Resolves `${env:NAME}` from environment variables. Missing variable throws `Environment variable 'NAME' is not set.` Always registered. |
| `keyvault` | `AzureKeyVaultSecretProvider` | Resolves `${keyvault:vault/secret}` from Azure Key Vault under the ambient Azure credential. |

The Key Vault locator must be `vault/secret`; anything else throws `Key Vault reference must be 'vault/secret', got '<locator>'.` A missing secret throws `Key Vault secret '<name>' was not found in vault '<vault>'.` Reads route through one `AzureKeyVaultSecretVault` per vault (public-cloud endpoint `https://{vaultName}.vault.azure.net/`); that same vault type implements the read/write/delete `ISecretVault` surface, where `GetSecretAsync` returns null on a 404 and `SetSecretAsync` returns the new secret version.

The ambient Azure identity is chosen by `SQLFLOW_AZURE_AUTH` (src/SqlFlow.Azure/AzureAuth.cs): `serviceprincipal`/`sp`, `managedidentity`/`mi`/`msi`, `azurecli`/`cli`/`azlogin`, and anything else falls back to the default credential chain. Service-principal mode uses the standard `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_CLIENT_SECRET` family.

## Resolution pipeline and the secretless policy

`ConnectionResolver.ResolveAsync` produces one `ResolvedConnection` per reference:

1. **Classify** the raw reference with `ConnectionRef.Parse`.
2. **Alias path**: if the store does not support aliases (the default `NullDataSourceStore`), throw. Otherwise resolve the `DataSource` registry entry, which carries the kind, capabilities, credential profile, storage context, and its own connection reference (itself a secretless inline string or a `${...}` reference, never a raw secret).
3. **Inline path**: the kind defaults to `DataSourceKind.MSSQL` unless the caller passes an explicit kind (the YAML `provider` key, or the CLI's `--provider` flag); capabilities, credential, and storage are empty.
4. **Expand** every `${...}` reference through the secret resolver.
5. **Canonicalize** through the `IConnectionStringCanonicalizer` registered for the kind, which also enforces the secretless policy. An unregistered kind throws `No connection-string canonicalizer is registered for data source kind '<kind>'.`

The secretless policy for an inline reference:

- A value that is exactly one whole `${scheme:locator}` reference (regex `^\$\{[a-zA-Z]+:[^}]+\}$`) gets `SecretlessPolicy.Trusted`: the whole string came from a secret store, so a password in it is legitimate.
- Any other literal gets `SecretlessPolicy.RequireSelfAuthenticating`: it must authenticate without a resting secret (Integrated Security or an `Authentication=Active Directory *` keyword).

For an alias, the policy follows the registry entry's `CredentialMode` (src/SqlFlow.Core/Connections/CredentialProfile.cs):

| CredentialMode | Policy |
|---|---|
| `InlineConnectionString` | `Trusted` (the whole string came from a secret store) |
| `InjectedToken` | `RejectRestingSecret` (a token is set at open time; the string carries no auth keyword) |
| `ConnectionStringAuth` (default), `Integrated` | `RequireSelfAuthenticating` |

The output, `ResolvedConnection` (src/SqlFlow.Core/Connections/ResolvedConnection.cs), carries:

- `CanonicalString`: the openable, canonicalized connection string and ADO.NET pool key. Treated as a secret; never logged, traced, or put in an exception message.
- `RedactedString`: the string with secret-bearing keywords removed; safe to log. `ToString()` returns it, so an accidental interpolation of the object cannot leak the canonical string.
- `Kind`, `Capabilities`, `Credential`, `Storage`.

`DataSourceKind` values are `MSSQL`, `AZDB`, `MySQL`, `PostgreSQL`, `Oracle`. There is deliberately no Synapse kind; Synapse-ness is a capability flag (`DataSourceCapabilities.IsSynapse`, alongside `SupportsCrossDbRef`, `IsLocal`, `ActivityMonitoring`), so kind and capability can never disagree. At open time, `CompositeConnectionFactory` dispatches by kind and an unregistered kind throws `No connection provider is registered for data source kind '<kind>'. Register the matching provider (for example from SqlFlow.Providers).`, never a silent null.

## Lightweight mode and `@alias`

The default engine wiring (`AddSqlFlowEngine` in src/SqlFlow.Execution/SqlFlowEngineServices.cs) registers `NullDataSourceStore` (`SupportsAliases => false`) as the shared `IConnectionResolver`'s store. A raw `@alias` value resolved through it, such as the CLI's ad-hoc `--source` and `--db` flags (there is no flow document to check against), is rejected with a precise error rather than a lookup failure. The resolver's check fires first:

```text
Connection '@x' is an alias and requires full mode (a configured control database). Supply an inline connection string or a ${...} secret reference instead.
```

A direct call into the null store throws the equivalent `Connection alias '@x' cannot be resolved in lightweight mode. Configure a control database (full mode) to use connection aliases, or supply an inline connection string or a ${...} secret reference.`

A relational document's own `connections:` block is a separate, per-document registry, not full mode: `target.server: dwh` (or `source.server` / `procedure.server`) resolves `dwh` against an in-memory store built from that document's own declared connections (`InMemoryDataSourceStore` in src/SqlFlow.Core/Connections/InMemoryDataSourceStore.cs, wired by `WithoutDatabaseResolver.Build` in src/SqlFlow.SqlServer/WithoutDatabaseResolver.cs), so a document with a `connections:` block runs with no control database at all. Full mode's own alias registry (`SqlDataSourceStore` in src/SqlFlow.SqlServer/FullMode/SqlDataSourceStore.cs) reads `flw.DataSource` from a real control database; no host in this codebase currently constructs it outside of tests (`sqlflow` and the worker both run without-database only).

## Secret hygiene: warnings and redaction

`SecretHygiene.LooksLikeEmbeddedSecret` flags a literal connection value containing any of these keywords, compared case-insensitively: `password=`, `pwd=`, `client secret=`, `clientsecret=`, `secret=`, `accesskey=`, `access key=`, `sharedaccesskey=`. Values starting with `${` or `@` are never flagged, because they are references, not literals.

The check runs on every document load through `DocumentLoader.Load` (src/SqlFlow.Execution/DocumentLoader.cs), which is the one load path both `sqlflow validate` and `sqlflow run` go through, so an embedded credential warns on the first local check, before a commit. The warning names the alternatives and never echoes the value:

```text
WARN  <file>: connection '<name>' embeds a credential in the document. Files under source control must carry references instead: use ${env:NAME}, ${keyvault:vault/secret}, or a bare '<name>:' (which resolves ${env:SQLFLOW_CONN_<NAME>}); put local values in the git-ignored .sqlflow/env file. See docs/environment-variables.md.
```

CLI flags that take a connection reference warn too: `sqlflow db --db`, `sqlflow healthcheck --source`, `sqlflow detect-unique-key --source`, `sqlflow runs cancel --db` (the direct-catalog break-glass route), and `sqlflow user reset-password --db` each print a warning when the flag value embeds a credential, because a literal on the command line lands in shell history. The warnings recommend the canonical variable (`${env:SQLFLOW_CATALOG_DB}` or `${env:SQLFLOW_SOURCE}`), an explicit `${env:NAME}` or `${keyvault:vault/secret}` reference, and the git-ignored `.sqlflow/env` file for local values.

`SecretHygiene.RedactedMessage` collapses each secret keyword's value (up to the next `;` or the end of the string) to `[redacted]` in any message that may quote external input, for example an exception wrapping a connection string. The CLI applies it to error paths in `sqlflow db`, the catalog run write-back, and other verbs, so third-party error text never leaks a credential into a report or log.

Source-control documents (`flowType: scm`) go further than a warning: `repository.secret` and `repository.username` must each be a whole `${...}` reference; a literal fails validation (src/SqlFlow.Yaml/YamlSourceControlFlowLoader.cs):

```text
<file>: 'repository.secret' must be a ${env:NAME} or ${keyvault:vault/secret} reference, never a literal. Put the value in the git-ignored .sqlflow/env file or your secret store.
```

## Local development: the `.sqlflow/env` file

`LocalEnvFile` (src/SqlFlow.Core/Secrets/LocalEnvFile.cs) loads the nearest `.sqlflow/env` file at the flow's directory or any parent and applies it to the process environment. Format: `KEY=VALUE` lines, `#` comments, blank lines, and optional surrounding single or double quotes on the value; names may contain only letters, digits, and `_`. The process environment always wins over the file, so CI and scheduler variables can never be shadowed by a stray local file. A malformed line fails loudly with the file path and line number, but the line content is deliberately never echoed (a malformed line in a secrets file may be the secret itself):

```text
<path>(<line>): expected KEY=VALUE (or a # comment).
```

## Configuration touchpoints

- **YAML**: the `connections:` block of relational documents (bare alias, string, or `provider`/`connection` map), the `target.connection` of file flows (a `${...}` reference or a literal only; a file flow has no alias form), and `repository.username`/`repository.secret` of scm documents.
- **CLI**: `sqlflow db --db <ref>`, `sqlflow runs cancel --db <ref>`, and `sqlflow user reset-password --db <ref>` (default `${env:SQLFLOW_CATALOG_DB}`), `sqlflow healthcheck --source <ref>` and `sqlflow detect-unique-key --source <ref>` (default `${env:SQLFLOW_SOURCE}`), and `--provider` for the inline kind of a non-SQL-Server source.
- **Environment**: `SQLFLOW_CONN_<NAME>` per bare alias, `SQLFLOW_AZURE_AUTH` (with `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_CLIENT_SECRET` for service-principal mode) for Key Vault access, and the git-ignored `.sqlflow/env` file for local values.

## Example

A health-check flow whose document carries only references (adapted from samples/healthcheck/orders-healthcheck.flow.yaml):

```yaml
flowType: hc
name: orders-watch
description: Watches order volume for missing or abnormal loads.

connections:
  dwh: ${env:SQLFLOW_DW}      # explicit reference; a bare 'dwh:' would resolve ${env:SQLFLOW_CONN_DWH}

target:
  server: dwh
  object: DW.dbo.Orders

dateColumn: OrderDate
metrics:
  - name: orders
    baseValue: COUNT(*)
```

Local values live next to the flow in the git-ignored `.sqlflow/env` file:

```text
# .sqlflow/env  (KEY=VALUE; process environment wins over this file)
SQLFLOW_DW=Server=localhost;Database=DW;Integrated Security=true;TrustServerCertificate=true;
```

The same reference forms work on the command line:

```bash
sqlflow validate orders-healthcheck.flow.yaml
sqlflow healthcheck --source '${env:SQLFLOW_DW}' --object DW.dbo.Orders
sqlflow db sync . --db '${keyvault:my-vault/catalog-db}'
```

## See also

- [Flow connections block](../flow/connections.md)
- [Environment variables](environment-variables.md)
- [CLI auth](../cli/auth.md)
- [Flow target](../flow/target.md)
