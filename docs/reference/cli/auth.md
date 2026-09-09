---
id: cli-auth
title: sqlflow auth and Azure authentication modes
type: cli-command
summary: Verify Azure authentication end to end; resolves SQLFLOW_AZURE_AUTH to a mode and acquires a real token for a chosen scope.
keywords:
  - auth
  - azure
  - sqlflow_azure_auth
  - managed identity
  - service principal
  - token
  - defaultazurecredential
  - diagnostic
cliCommand: auth
related:
  - concept-connections-and-secrets
  - source-type-duckdb
  - flow-inv
  - flow-service-principals
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Azure/AzureAuth.cs
  - src/SqlFlow.Azure/AzureCredentialFactory.cs
  - src/SqlFlow.Azure/AzureEnvironment.cs
  - src/SqlFlow.Azure/AzureStorageCredentialProvider.cs
---

# sqlflow auth

## Synopsis

```bash
sqlflow auth [--scope storage|keyvault|arm|<uri>]
```

## Description

Verifies Azure authentication end to end in the current environment. The command takes no pipeline file; it is a pure environment check. It reports the raw value of the `SQLFLOW_AZURE_AUTH` environment variable and the auth mode it resolves to, then actually acquires a token for the chosen scope through `IAzureCredentialFactory`, the shared credential factory that Key Vault secret resolution uses for every vault call and that ADF/Automation invoke uses whenever an invoke service principal carries no explicit secret reference. DuckDB cloud object-storage reads resolve their credential through the storage credential provider (src/SqlFlow.Azure/AzureStorageCredentialProvider.cs), which reads the same `SQLFLOW_AZURE_AUTH` intent and the same in-Azure detection. A successful token from `sqlflow auth` therefore confirms the deployment's ambient credential and the one auth intent every one of those paths reads, before you run a cloud flow.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| (none) | n/a | `sqlflow auth` takes no positional arguments and no pipeline file. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--scope` | string | `storage` | Token audience. An alias or a verbatim scope URI; see the alias table below. |

`--scope` aliases (matched case-insensitively):

| Value | Resolved scope URI |
| --- | --- |
| `storage`, `adls`, `blob` | `https://storage.azure.com/.default` |
| `keyvault`, `vault` | `https://vault.azure.net/.default` |
| `arm`, `management` | `https://management.azure.com/.default` |
| anything else | used verbatim as the scope URI |

## Authentication modes: SQLFLOW_AZURE_AUTH

`SQLFLOW_AZURE_AUTH` is parsed in exactly one place (`AzureAuth.Mode()` in src/SqlFlow.Azure/AzureAuth.cs) and shared by the .NET credential factory and the cloud-storage credential provider, so Key Vault, invoke executors, and DuckDB object storage obey one auth intent. Values are trimmed and matched case-insensitively:

| Value | Mode | Credential built |
| --- | --- | --- |
| `serviceprincipal`, `sp` | ServicePrincipal | `ClientSecretCredential` from `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`. A missing variable throws `Service-principal auth requires environment variable '<name>'.` |
| `managedidentity`, `mi`, `msi` | ManagedIdentity | `ManagedIdentityCredential`. A non-blank `AZURE_CLIENT_ID` selects that user-assigned identity; otherwise the system-assigned identity is used. |
| `azurecli`, `cli`, `azlogin` | AzureCli | `AzureCliCredential` (uses the `az login` session). |
| any other value, or unset | DefaultChain | `DefaultAzureCredential` with the options described below. |

### The default chain and in-Azure detection

In DefaultChain mode the credential is `DefaultAzureCredential` with options from `AzureEnvironment.DefaultCredentialOptions()`:

- The managed-identity credential is excluded when the process is not running in Azure. This avoids the chain stalling on the IMDS probe, which has no endpoint on a developer machine.
- The environment, Azure CLI, Visual Studio, VS Code, and interactive browser credentials are all enabled, so a signed-in developer authenticates with no configuration.

Whether the process is "in Azure" is decided by `AzureEnvironment.IsRunningInAzure()`:

- The explicit override `IS_RUNNING_IN_AZURE` wins in both directions when set: `true` (case-insensitive) forces in-Azure on; any other non-blank value forces it off.
- Otherwise the process counts as in Azure when any of these variables is non-empty: `WEBSITE_INSTANCE_ID`, `FUNCTIONS_WORKER_RUNTIME`, `AZURE_CONTAINER_INSTANCE_ROOT_PATH`, `KUBERNETES_SERVICE_HOST`, `AZURE_VM_RESOURCE_GROUP`, `MSI_ENDPOINT`, `MSI_SECRET`, `IDENTITY_HEADER`, `APPSETTING_WEBSITE_SITE_NAME`.

The same in-Azure decision drives the DuckDB cloud-storage credential chain, so every Azure path agrees.

## Behavior and output

The command prints, in order:

1. `SQLFLOW_AZURE_AUTH: <raw value or (unset)> -> <resolved mode>`. For DefaultChain the label explains the chain composition: in Azure it reads `default chain (managed identity -> az CLI -> env)`; off-cloud it reads `default chain (az CLI -> env; managed identity excluded off-cloud)`.
2. `Acquiring a token for scope: <resolved scope URI>`.
3. On success: `OK   acquired a token (expires <timestamp> UTC). Azure auth works.`
4. On failure, to stderr: `FAIL could not acquire a token: <underlying message>`. The failure is reported with the cause message only, no stack trace, because the failure itself is the diagnostic answer.

## Examples

Check the default storage scope on a developer machine with an `az login` session:

```bash
sqlflow auth
```

```text
SQLFLOW_AZURE_AUTH: (unset) -> default chain (az CLI -> env; managed identity excluded off-cloud)
Acquiring a token for scope: https://storage.azure.com/.default
OK   acquired a token (expires 2026-07-02 13:05:11Z UTC). Azure auth works.
```

Verify that a CI service principal can reach Key Vault before running a flow that resolves Key Vault secrets:

```bash
export SQLFLOW_AZURE_AUTH=sp
export AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000
export AZURE_CLIENT_ID=11111111-1111-1111-1111-111111111111
export AZURE_CLIENT_SECRET=...
sqlflow auth --scope keyvault
```

Verify a user-assigned managed identity on an Azure VM, against a verbatim scope URI:

```bash
export SQLFLOW_AZURE_AUTH=mi
export AZURE_CLIENT_ID=22222222-2222-2222-2222-222222222222
sqlflow auth --scope https://management.azure.com/.default
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | A token was acquired for the resolved scope. |
| 1 | Token acquisition failed; the cause is printed to stderr as `FAIL could not acquire a token: <message>`. |

## See also

- [Connections and secrets](../concepts/connections-and-secrets.md): Key Vault references resolve through this same credential.
- [DuckDB source](../flow/source-types/duckdb.md): cloud object-storage reads authenticate through the same auth intent.
- [flowType: inv](../flow/inv.md): invoke service principals without a secret reference authenticate as the deployment's ambient identity through this same factory.
- [Service principals for flows](../flow/service-principals.md).
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
