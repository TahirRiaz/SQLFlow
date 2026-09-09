---
id: flow-target
title: "File flow: target section"
type: flow-reference
summary: "The target section of a file flow: required connection, schema, and table keys naming the SQL Server table the load writes to."
keywords:
  - target.connection
  - target.schema
  - target.table
  - connection reference
  - secret reference
  - qualified name
  - sql server target
yamlPath: target
related:
  - flow-source
  - concept-connections-and-secrets
  - flow-schema
sourceRefs:
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Secrets/SecretResolver.cs
  - src/SqlFlow.Core/Secrets/EnvSecretProvider.cs
  - src/SqlFlow.Azure/AzureKeyVaultSecretProvider.cs
  - src/SqlFlow.SqlServer/SqlBulkLoader.cs
  - src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Core/Secrets/SecretHygiene.cs
  - src/SqlFlow.Cli/Program.cs
---

# File flow: target section

The `target` section of a file flow names the SQL Server table the pipeline writes to: which server and database (`connection`), which schema (`schema`), and which table (`table`). The target of a file flow is always SQL Server; the load stack is `SqlBulkCopy` for rows plus T-SQL DDL for table creation and evolution (src/SqlFlow.SqlServer/SqlBulkLoader.cs, src/SqlFlow.Core/Engine/FlowRunner.cs). The section maps to the `TargetSpec` record in src/SqlFlow.Core/Model/FlowDefinition.cs and is validated by src/SqlFlow.Yaml/YamlFlowLoader.cs.

```yaml
name: orders
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `connection` | string | yes | none | SQL Server connection string, either raw or containing `${scheme:locator}` secret references resolved at run time. |
| `schema` | string | yes | none | Target schema name, unbracketed (for example `dbo`). |
| `table` | string | yes | none | Target table name, unbracketed. Created or evolved by the run according to the flow's `schema` policy. |

Key names are camelCase in YAML. Unknown keys inside `target` are ignored by the loader (it deserializes with unmatched properties ignored), so a misspelled key is not reported as unknown; the load fails with the missing-key error below instead.

The `target` mapping itself is required. A flow document without it fails to load:

```text
orders.flow.yaml: 'target' is required.
```

Each of the three keys is a required, non-blank string (whitespace-only values count as missing). A missing key fails with the key's dot-path in the message, for example:

```text
orders.flow.yaml: 'target.connection' is required.
```

The same pattern applies to `'target.schema'` and `'target.table'`.

### connection

A SQL Server connection string. It can be written raw, but the project convention is that secrets never live in the pipeline file: the value carries one or more `${scheme:locator}` references that are expanded at run time by `SecretResolver` (src/SqlFlow.Core/Secrets/SecretResolver.cs). Two provider schemes ship with the engine:

- `${env:NAME}` reads the environment variable `NAME` (src/SqlFlow.Core/Secrets/EnvSecretProvider.cs, always registered). A missing variable fails with `Environment variable 'NAME' is not set.`
- `${keyvault:vault/secret}` reads a secret from Azure Key Vault under the ambient Azure credential (src/SqlFlow.Azure/AzureKeyVaultSecretProvider.cs). The locator must be `vault/secret`; a malformed locator fails with `Key Vault reference must be 'vault/secret', got '...'.` and a missing secret with `Key Vault secret '...' was not found in vault '...'.`

References can appear anywhere inside the string and mix with literal text, so a partial reference such as `Server=.;Database=DW;Password=${keyvault:my-vault/dw-pwd};...` also works. An unregistered scheme fails with `No secret provider is registered for scheme '...' (referenced by '...').`

A raw value that embeds a credential-bearing keyword (`password=`, `pwd=`, `secret=`, `accesskey=`, and variants, compared without case) still loads, but both `sqlflow validate` and `sqlflow run` print a `WARN` line naming the file and the reference alternatives; the credential value itself is never echoed. Values starting with `${` are exempt from the check (src/SqlFlow.Execution/DocumentLoader.cs, src/SqlFlow.Core/Secrets/SecretHygiene.cs).

Resolution timing: `sqlflow validate` only loads and validates the document, so it never resolves the connection or touches the server. `sqlflow plan` and `sqlflow run` both resolve the reference and connect (plan introspects the target table to compute DDL; run additionally loads). For local development, a git-ignored `.sqlflow/env` file (searched upward from the flow file's directory) supplies values for `${env:...}` references; variables already set in the process environment always win (src/SqlFlow.Cli/Program.cs).

### schema

The target schema name, written without brackets. Target introspection passes it verbatim as a query parameter (the `INFORMATION_SCHEMA.COLUMNS` lookup in src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs); whenever the engine renders the full table name (DDL statements, the bulk-copy destination, `TRUNCATE TABLE`) the name is bracket-quoted (see `table` below).

### table

The target table name, written without brackets. The table does not need to pre-exist: all three `schema.evolve` policies (`create`, `widen`, `strict`) create it when it is missing.

`TargetSpec.QualifiedName` renders the full name as `[Schema].[Table]`, escaping every `]` in either part as `]]` (src/SqlFlow.Core/Model/FlowDefinition.cs, covered by tests/SqlFlow.Core.Tests/TargetSpecTests.cs). For example `schema: dbo`, `table: My]Tbl` renders as `[dbo].[My]]Tbl]`. The qualified name is what the engine uses as the `SqlBulkCopy` destination table and in the `TRUNCATE TABLE` statement issued by `load.mode: truncate-load` (src/SqlFlow.SqlServer/SqlBulkLoader.cs).

## Full example

Adapted from samples/quickstart/orders.flow.yaml, with the connection stored in Azure Key Vault instead of an environment variable:

```yaml
name: orders
source:
  type: csv
  location: ./orders.csv
  options:
    delimiter: ","
    header: true

# Secrets are never stored in the file; the reference is resolved at run time.
target:
  connection: ${keyvault:df-prod-vault/dw-connection}
  schema: dbo
  table: Orders

schema:
  evolve: widen

load:
  mode: append
  batchSize: 50000
```

```bash
sqlflow validate orders.flow.yaml   # checks the definition, does not resolve the connection
sqlflow plan     orders.flow.yaml   # resolves the connection, previews the exact SQL
sqlflow run      orders.flow.yaml   # creates/evolves the table and loads
```

## See also

- [File flow: source section](./source.md)
- [File flow: schema section](./schema.md)
- [Connections and secrets](../concepts/connections-and-secrets.md)
