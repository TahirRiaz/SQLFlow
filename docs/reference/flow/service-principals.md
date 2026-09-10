---
id: flow-service-principals
title: servicePrincipals block
type: flow-reference
summary: Declare secretless Azure service principals that invoke definitions reference by name for ADF pipeline and Automation runbook triggers.
keywords:
  - service principal
  - tenantid
  - clientsecret
  - keyvault
  - subscription
  - resource group
  - datafactoryname
  - automationaccountname
yamlPath: servicePrincipals
related:
  - flow-inv
  - cli-auth
  - concept-connections-and-secrets
sourceRefs:
  - src/SqlFlow.Yaml/YamlInvokeParts.cs
  - src/SqlFlow.Yaml/InvokeYaml.cs
  - src/SqlFlow.Core/Connections/ServicePrincipalProfile.cs
  - src/SqlFlow.Azure/Invoke/AzureServicePrincipalResolver.cs
  - src/SqlFlow.Core/Secrets/ISecretResolver.cs
  - src/SqlFlow.Core/Secrets/SecretResolver.cs
  - samples/invoke/trigger-refresh.flow.yaml
  - tests/SqlFlow.Core.Tests/AzureServicePrincipalResolverTests.cs
---

# servicePrincipals block

The top-level `servicePrincipals:` block declares named, secretless Azure service-principal profiles. Each entry carries the Azure coordinates (tenant, client, subscription, resource group, and the Data Factory or Automation account name) that an invoke needs to trigger an Azure Data Factory pipeline or an Azure Automation runbook. Invoke definitions reference an entry by its name via their `servicePrincipal` key. The block is available in the standalone invoke document (`flowType: inv`) and in the ing, exp, and sp documents. In those three document kinds, the same `invokes:` entries serve as hooks around the load: `preInvoke` and `postInvoke` in ing, `postInvoke` only in exp and sp. All four document kinds share one mapping and validation path in src/SqlFlow.Yaml/YamlInvokeParts.cs.

Secrets never rest in the document. The `clientSecret` key accepts only a whole `${env:NAME}` or `${keyvault:vault/secret}` reference, resolved transiently at runtime. Omitting `clientSecret` entirely is the preferred production posture: the deployment's ambient Azure credential (managed identity, `az login`) is used instead.

## Minimal example

```yaml
flowType: inv
name: trigger-refresh

servicePrincipals:
  deploy:
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod

invoke:
  type: adf
  pipeline: pl_refresh_marts
  servicePrincipal: deploy
```

No `clientSecret` is set, so the run authenticates with the ambient Azure credential.

## Keys reference

Each entry under `servicePrincipals:` is a map keyed by the principal's name. Names may use letters, digits, `_`, `.`, and `-`; lookup is case-insensitive.

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `tenantId` | string | only when `clientSecret` is set | none | Azure AD tenant id (a coordinate, not a secret). |
| `clientId` | string | only when `clientSecret` is set | none | Application (client) id (a coordinate, not a secret). |
| `clientSecret` | string | no | none (ambient credential) | Whole `${env:NAME}` or `${keyvault:vault/secret}` reference to the client secret. Literal values are rejected. |
| `subscriptionId` | string | yes | none | Azure subscription id (ARM context). |
| `resourceGroup` | string | yes | none | Azure resource group name. |
| `dataFactoryName` | string | required by `adf` invokes that reference this entry | none | Data Factory instance name, used by the ADF executor. |
| `automationAccountName` | string | required by `aut` invokes that reference this entry | none | Automation account name, used by the Automation executor. |
| `keyVaultName` | string | no | none | The Key Vault that backs the `clientSecret` reference; a coordinate/hint, not a secret. |

All string values are trimmed; blank values are treated as absent.

### Entry names

A name must be non-empty and consist of ASCII letters, digits, `_`, `.`, or `-`. An invalid name fails validation with:

```text
service-principal name '<name>' is invalid. Use letters, digits, '_', '.', or '-'.
```

Names are compared case-insensitively, so two entries whose names differ only in case collide. A duplicate fails with:

```text
service principal '<name>' is declared more than once.
```

An entry declared with an empty value (no fields) fails with:

```text
'servicePrincipals.<name>' must be a map of service-principal fields.
```

### clientSecret

The value must match `^\$\{[a-zA-Z]+:[^}]+\}$`: a single, whole `${scheme:payload}` reference with nothing before or after it. In practice the supported schemes are `env` and `keyvault`. Any other shape, including a literal secret value, fails with:

```text
'servicePrincipals.<name>.clientSecret' must be a whole ${env:NAME} or ${keyvault:vault/secret} reference; the secret value itself never rests in the document. Omit it to authenticate with the ambient Azure credential.
```

The reference is stored as-is (`ClientSecretRef` on `ServicePrincipalProfile` in src/SqlFlow.Core/Connections/ServicePrincipalProfile.cs) and expanded at run time by src/SqlFlow.Azure/Invoke/AzureServicePrincipalResolver.cs through the shared `ISecretResolver` (src/SqlFlow.Core/Secrets/ISecretResolver.cs, implemented by src/SqlFlow.Core/Secrets/SecretResolver.cs). When the reference resolves to an empty value the run fails with:

```text
Service principal '<name>' resolved an empty client secret from '<reference>'.
```

When `clientSecret` is omitted, the resolver builds the deployment's ambient credential instead of a `ClientSecretCredential`; `tenantId` and `clientId` are then unnecessary.

### tenantId and clientId

Optional coordinates, except when `clientSecret` is set: a client-secret credential needs both, and omitting either fails at load with:

```text
'servicePrincipals.<name>' sets clientSecret, so tenantId and clientId are required.
```

### subscriptionId and resourceGroup

Always required. A missing or blank value fails with:

```text
'servicePrincipals.<name>.subscriptionId' is required.
```

```text
'servicePrincipals.<name>.resourceGroup' is required.
```

### dataFactoryName and automationAccountName

Optional on the entry itself, but enforced cross-field when an invoke references the entry: an `adf` invoke requires the referenced principal to carry `dataFactoryName`, and an `aut` invoke requires `automationAccountName`. The mismatches fail at load with:

```text
'<section>' is an adf invoke, but service principal '<name>' has no dataFactoryName.
```

```text
'<section>' is an aut invoke, but service principal '<name>' has no automationAccountName.
```

An invoke that references an undeclared name fails with:

```text
'<section>.servicePrincipal' references '<name>', which is not declared under 'servicePrincipals:'.
```

## Full example

Adapted from samples/invoke/trigger-refresh.flow.yaml.

```yaml
flowType: inv
name: trigger-refresh

servicePrincipals:
  deploy:
    tenantId: 00000000-0000-0000-0000-000000000000
    clientId: 00000000-0000-0000-0000-000000000000
    clientSecret: ${env:SQLFLOW_DEPLOY_SP_SECRET}
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod
  ops:
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-ops
    automationAccountName: aa-prod

invoke:
  type: adf
  pipeline: pl_refresh_marts
  servicePrincipal: deploy
  parameters:
    environment: prod
    fullLoad: true
    batchSize: 5000
```

The `deploy` principal authenticates with a client-secret credential resolved from the `SQLFLOW_DEPLOY_SP_SECRET` environment variable; the `ops` principal omits the secret and would use the ambient Azure credential. An `aut` invoke referencing `ops` would trigger a runbook in the `aa-prod` Automation account.

## See also

- [flowType: inv](./inv.md): the standalone invoke document that consumes this block.
- [Connections and secrets](../concepts/connections-and-secrets.md): the `${env:...}` and `${keyvault:...}` reference schemes.
- [auth](../cli/auth.md): CLI authentication against Azure.
