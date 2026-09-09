# SQLFlow as a drop-in for Azure Data Factory

> This document has not been re-verified against the current codebase. For the current,
> code-verified behavior of the invoke flow and control plane it describes, see
> [docs/reference/flow/inv.md](reference/flow/inv.md) and
> [docs/reference/concepts/control-plane.md](reference/concepts/control-plane.md).

SQLFlow runs inside your ADF pipelines as a thin trigger, not as a container ADF boots per run. The model is the
proven one: ADF calls an always-on custom API to start work. Here that API is the **SQLFlow control plane**, and the
ADF side is a small, copyable pipeline of Web Activities.

A trigger is a sub-second authenticated call. Nothing is provisioned when ADF triggers a flow: the control plane is
already warm, and the flow executes on the control plane's own worker (or a self-hosted worker you run). So the
heavy lifting stays on the warm SQLFlow tier, and your ADF pipeline just says "run this flow and wait."

## What's in this folder

| Artifact | What it is |
|---|---|
| `Dockerfile` (repo root) | Builds the control plane into a container image. |
| `deploy/bicep/control-plane.bicep` | Deploys that image as an always-on Azure Container App, with secrets read from Key Vault by its managed identity. |
| `deploy/adf/SqlFlowTriggerFlow.pipeline.json` | The ADF pipeline you import: authenticate, trigger a run, poll to completion, fail if the run failed. |

## 1. Build and push the image

```bash
docker build -t <registry>.azurecr.io/sqlflow-control-plane:latest .
az acr login -n <registry>
docker push <registry>.azurecr.io/sqlflow-control-plane:latest
```

## 2. Deploy the control plane

The control plane needs a catalog database (the EF-managed shadow), a JWT signing key, and an identity that can read
both from Key Vault. Put the two secrets in Key Vault first:

```bash
az keyvault secret set --vault-name <kv> --name sqlflow-catalog-db      --value "<ADO.NET connection string>"
az keyvault secret set --vault-name <kv> --name sqlflow-jwt-signing-key --value "<a random string of >= 32 bytes>"
```

Then deploy. The template creates a user-assigned identity, grants it **Key Vault Secrets User**, and wires the app
to pull those secrets at startup. That same identity is what the engine uses to resolve `${keyvault:...}` references
and cloud-storage credentials at run time (`SQLFLOW_AZURE_AUTH=mi`), so no secret value is ever placed in app
configuration.

```bash
az deployment group create -g <rg> -f deploy/bicep/control-plane.bicep \
  -p managedEnvironmentId=<container-apps-env-id> \
     image=<registry>.azurecr.io/sqlflow-control-plane:latest \
     keyVaultName=<kv> \
     acrLoginServer=<registry>.azurecr.io
```

After deploy:

- The output `controlPlaneBaseUrl` is what you pass to the ADF pipeline.
- Grant the output `identityClientId` access to the SQL databases your flows touch (and to any flow secrets), since
  the control-plane worker resolves every credential from its own environment.
- Run the catalog migration once (`sqlflow db migrate` against the catalog connection), and register your git repos
  as managed sources (`POST /api/v1/repos/sources`) so the catalog stays synced from git.

## 3. Import the ADF pipeline

Import `deploy/adf/SqlFlowTriggerFlow.pipeline.json` in ADF Studio (Author -> Pipelines -> ... -> Import from
pipeline template), or deploy it as a `Microsoft.DataFactory/factories/pipelines` resource. Set its parameters:

| Parameter | Value |
|---|---|
| `controlPlaneBaseUrl` | the deploy output, e.g. `https://sqlflow-control-plane.<region>.azurecontainerapps.io` |
| `repoId` | the repo id from `GET /api/v1/repos` |
| `flowName` | the flow to run |
| `bootstrapSecret` | the control plane's bootstrap secret |

The pipeline authenticates, triggers the run, polls `GET /api/v1/runs/{id}` every `pollIntervalSeconds` until the run
reaches a terminal state, and **fails the ADF activity** (`SqlFlowRunFailed`) if the run did not succeed - so a failed
flow surfaces as a failed ADF pipeline.

## Production notes

- **Secrets in ADF.** The template takes `bootstrapSecret` as a secure-string parameter for simplicity. In
  production, source it from a **Key Vault linked service** instead of a parameter value, and prefer issuing scoped,
  revocable API keys over reusing the bootstrap secret once that capability ships.
- **Long-running flows and token lifetime.** The pipeline fetches one token up front, and a bootstrap token does not
  renew: only an interactive sign-in rolls onto a fresh token. If a flow can run longer than the control plane's
  `ControlPlane:Jwt:AccessTokenMinutes` (default 720), either raise that value or move the `GetToken` step inside the
  polling loop so each poll re-authenticates.
- **Routing.** To pin a flow to a specific worker pool, add `"pool": "<pool>"` to the `TriggerRun` body; to run an
  exact committed version, add `"commitSha": "<sha>"`.
- **Native dependencies.** The image installs `libssl3` for LibGit2Sharp (SHA-pinned git materialization). If you
  disable git materialization you can drop it.
