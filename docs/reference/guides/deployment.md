---
id: guide-deployment
title: "Deploying: docker compose, Kubernetes, and ADF integration"
type: guide
summary: Deploy the control plane, workers, and GUI with docker compose or Kubernetes (KEDA), and trigger flows from Azure Data Factory Web Activities.
keywords:
  - docker compose
  - kubernetes
  - keda
  - hpa
  - adf
  - web activities
  - images
  - worker pools
  - ingress
related:
  - concept-control-plane
  - cli-worker
  - concept-authentication-and-identity
sourceRefs:
  - deploy/README.md
  - deploy/compose/docker-compose.yml
  - deploy/compose/.env.example
  - Dockerfile.worker
  - deploy/docker/worker-entrypoint.sh
  - deploy/k8s/controlplane.yaml
  - deploy/k8s/worker-pool.yaml
  - deploy/k8s/gui.yaml
  - deploy/k8s/ingress.yaml
  - deploy/k8s/secrets.example.yaml
  - deploy/bicep/control-plane.bicep
  - deploy/bicep/worker.bicep
  - deploy/bicep/gui.bicep
  - deploy/bicep/ai-foundry.bicep
  - deploy/bicep/main.bicep
  - deploy/adf/SqlFlowTriggerFlow.pipeline.json
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.Cli/Program.cs
  - docs/adf-dropin.md
---

# Deploying: docker compose, Kubernetes, and ADF integration

SQLFlow ships as three container images with three distinct scaling behaviors:

| Image | Built from | Scales on | Behind the ingress? |
|---|---|---|---|
| `sqlflow-control-plane` | `Dockerfile` (repo root) | one replica (dispatch has one owner) | yes, under `/api` |
| `sqlflow-gui` | `gui/Dockerfile` | trivially (static SPA) | yes, under `/` |
| `sqlflow-worker` | `Dockerfile.worker` | the control plane's replica target (KEDA) | never (pull model, no inbound surface) |

```bash
docker build -t sqlflow-control-plane:latest .
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker build -t sqlflow-gui:latest gui/
```

The split matters because the two tiers have opposite traffic shapes. The control plane is an HTTP API that also owns the run queue: it runs as one replica and sits behind the ingress. Workers are compute nodes that pull work from the control plane's dispatcher over outbound HTTP only; they expose nothing, never sit behind an ingress, and scale on the replica target the control plane computes from its backlog and its fleet. `ControlPlane__Worker__Enabled=false` is the switch that keeps the two independent: it turns off the control plane's in-process worker so API replicas do API work only (see `src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs`, `WorkerOptions`; the default is `true`, which is the single-node mode).

## Local / single host: docker compose

`deploy/compose/docker-compose.yml` runs the full stack: SQL Server (catalog plus a demo sink), an API-only control plane, one scalable worker, and the GUI.

```bash
cd deploy/compose
cp .env.example .env    # set the secrets
docker compose up -d --build
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

- GUI: `http://localhost:8081` (sign in with the bootstrap admin from `.env`)
- Control plane API: `http://localhost:5000` (OpenAPI at `/openapi/v1.json`)
- SQL Server: published on host port `14333` for host-side tooling only

On first start, bootstrap provisioning creates the catalog database, applies EF migrations, seeds the built-in roles, and creates the initial admin from `.env`. Bootstrap is idempotent: restarts converge to the same state and never reset an existing admin's password.

### Required .env values

From `deploy/compose/.env.example`; the compose file fails fast (`:?` expansion) when a required one is missing:

| Variable | Required | Meaning |
|---|---|---|
| `MSSQL_SA_PASSWORD` | yes | SQL Server `sa` password, also used in the stack's connection strings. SQL Server enforces complexity. |
| `SQLFLOW_JWT_SIGNING_KEY` | yes | HS256 signing key for control-plane tokens, at least 32 bytes of randomness. |
| `SQLFLOW_BOOTSTRAP_SECRET` | yes | Break-glass bootstrap secret, at least 32 bytes. Used to provision users, then rotate or remove. |
| `SQLFLOW_ADMIN_PASSWORD` | yes | The initial admin's password (at least 12 characters). |
| `SQLFLOW_ADMIN_USERNAME` | no | Defaults to `admin`. |
| `SQLFLOW_WORKER_POOL` | no | Pool(s) the worker service serves; empty means untargeted runs only. |
| `SQLFLOW_GIT_TOKEN` | no | Token for private git remotes (SHA-pinned materialization). |

The 32-byte minimums for the signing key and bootstrap secret are enforced at startup by `ControlPlaneOptions.Validate()`; a shorter value stops the control plane from starting with a clear error.

### How the compose layout is wired

- The control plane runs API-only: `ControlPlane__Worker__Enabled: "false"`. Compute belongs to the separate `worker` service, which you scale with `--scale worker=N`.
- The GUI is a separate origin in this layout (`http://localhost:8081` vs `http://localhost:5000`), so the control plane carries `ControlPlane__Cors__AllowedOrigins__0: http://localhost:8081` and the GUI reads `SQLFLOW_API_BASE_URL: http://localhost:5000`. Production ingress layouts route both under one host and drop the CORS configuration entirely.
- The control plane container health check probes `/health/live`. Liveness has no dependencies; readiness (`/health/ready`) probes the catalog database via an EF DbContext check. Both endpoints are exempt from the rate limiter.
- The demo flows reference their sink via `${env:SQLFlowSinkConStr}`, so both the control plane and the worker carry that variable.

## The worker image

`Dockerfile.worker` publishes `src/SqlFlow.Cli/SqlFlow.Cli.csproj` (framework-dependent, inside the Linux SDK image so the linux-x64 native assets for LibGit2Sharp and the DuckDB reader ship under `runtimes/linux-x64`) and installs `libssl3` on the Debian runtime image because LibGit2Sharp's bundled native git needs OpenSSL for HTTPS remotes.

The entrypoint, `deploy/docker/worker-entrypoint.sh`, composes the `sqlflow worker` invocation from the environment. The control plane URL and the node token are the CLI's own environment defaults (`SQLFLOW_URL`, `SQLFLOW_TOKEN`), so neither appears on the command line or in `ps` output. A worker needs no catalog connection at all: the run's definition, its snapshotted YAML, its lineage context and its live trace travel over the node protocol, so the only things a node reaches are the control plane, the data its flows touch, and (for a pinned run without a snapshot) the git remote. Configuration is environment-only, matching the worker's "credentials live on the node" model:

| Variable | Required | Maps to | Meaning |
|---|---|---|---|
| `SQLFLOW_URL` | yes | the CLI's default `--url` | The control plane the node polls for work over the node protocol. |
| `SQLFLOW_TOKEN` | yes | the CLI's default `--token` | A personal access token minted with the `node` scope (or a `${env:...}`/`${keyvault:...}` reference to one). |
| `SQLFLOW_WORKER_POOL` | no | `--pool` | Comma-separated pools this node serves; empty means untargeted runs only. |
| `SQLFLOW_WORKER_POLL_SECONDS` | no | `--poll-seconds` | How long each poll waits for work before returning empty; the CLI default is 30. |
| `SQLFLOW_WORKER_DRAIN_SECONDS` | no | `--drain-seconds` | How long a stopping node finishes the runs it already holds; the CLI default is 540. Must stay below the platform's termination grace period (see below). |
| `SQLFLOW_GIT_TOKEN` | no | (read by git materialization) | Token for private git remotes. |
| every `${env:...}` reference the flows use | per estate | secret resolver | Source and target connection strings resolve on the node, never in the control plane. |

The underlying CLI command is `sqlflow worker --url <control-plane> [--token <ref>] [--poll-seconds N] [--pool a,b] [--drain-seconds N]` (see `src/SqlFlow.Cli/Program.cs`). Placement is the control plane dispatcher's, so any number of concurrent workers is safe.

The node token can only be minted once the control plane is running: sign in as the admin and call `POST /api/v1/me/tokens` with scopes `["node"]` (or use the GUI's token page), then place the secret where the worker's environment reads it (`.env` for compose, the `sqlflow-secrets` key `node-token` on Kubernetes, the Key Vault secret `sqlflow-node-token` on Azure). A first deployment therefore brings up the control plane first and adds the worker once the token exists; a worker deployed without one starts, logs that it has no credential, and takes no work.

### Scale-in must not sever runs: the grace period is not optional

Workers are scaled in routinely, and the platform picks its victims blindly: a replica executing a six-minute bulk copy is as likely to be reclaimed as an idle one. The worker handles SIGTERM by draining (it stops taking work and lets the runs it already holds finish and report their outcomes, polling throughout so their leases stay alive), but that only works if the orchestrator actually waits.

**Set the termination grace period above `--drain-seconds` on every worker workload.** Both Kubernetes and Container Apps default to 30 seconds, which is shorter than most real loads:

| Platform | Setting | Estate value |
|---|---|---|
| Kubernetes | `spec.template.spec.terminationGracePeriodSeconds` (`deploy/k8s/worker-pool.yaml`) | 600 |
| Azure Container Apps | `properties.template.terminationGracePeriodSeconds` (`deploy/bicep/worker.bicep`, or `az containerapp update --termination-grace-period`) | 600 |

Leaving it at the default does not disable the drain, it truncates it: the worker begins draining, the platform kills it 30 seconds later, and every run that needed longer is severed with no outcome recorded. Each severed run is then requeued by the reaper, which **consumes one of its three execution attempts**. A run caught by three scale-ins is failed permanently and reported as though the run itself were at fault. This is the single most common way a healthy flow acquires a mysterious "Run interrupted" failure.

Note that `az containerapp update --image` (what most deploy scripts run) does **not** set this property. It has to be applied through the bicep or an explicit `--termination-grace-period` update, and it survives subsequent image swaps once set.

## Kubernetes: deploy/k8s

Prerequisites: an ingress controller (the annotations assume ingress-nginx), cert-manager or another TLS source, and [KEDA](https://keda.sh) for worker autoscaling.

```bash
kubectl apply -f deploy/k8s/namespace.yaml
# create the real secret (see secrets.example.yaml for the required keys), then:
kubectl apply -f deploy/k8s/controlplane.yaml
kubectl apply -f deploy/k8s/gui.yaml
kubectl apply -f deploy/k8s/ingress.yaml
kubectl apply -f deploy/k8s/worker-pool.yaml
```

### Secrets

`deploy/k8s/secrets.example.yaml` documents the keys every deployment references (create the real secret out of band, never commit values):

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: sqlflow-secrets
  namespace: sqlflow
type: Opaque
stringData:
  catalog-connection: "Server=your-sql-server;Database=SqlFlowCatalog;User ID=sqlflow;Password=CHANGE_ME;TrustServerCertificate=True"
  jwt-signing-key: "CHANGE_ME_32+_RANDOM_BYTES________"
  bootstrap-secret: "CHANGE_ME_32+_RANDOM_BYTES________"
  admin-password: "CHANGE_ME_12+_CHARS"
  git-token: ""
  sink-connection: "Server=your-sql-server;Database=SqlFlowDemo;User ID=sqlflow;Password=CHANGE_ME;TrustServerCertificate=True"
```

Secrets stay on the tier that uses them: the control plane gets the catalog connection and JWT material; workers additionally get the git token and every `${env:...}` connection their pool's flows reference. Nothing data-plane ever passes through the control plane.

### One host, path split, no CORS

`deploy/k8s/ingress.yaml` routes `/api` and `/openapi` to the control plane service and `/` to the GUI service under a single host. The SPA is same-origin with the API, so no CORS configuration exists anywhere; the GUI deployment sets `SQLFLOW_API_BASE_URL=""`, which means "same origin". Add `ControlPlane__Cors__AllowedOrigins__0` only if the GUI is served from another host.

### Control plane: one API-only replica

`deploy/k8s/controlplane.yaml` runs one replica, on purpose. The run queue lives in the control plane process and is owned by exactly one replica at a time through the dispatch ownership lease (`catalog.DispatchLease`); any other replica serves the API and journals enqueues for the owner's reconcile, but answers every node call with 503 `Dispatch is not active on this replica` and a retry hint. The nodes retry every call (polls with backoff, and the flow-version, context, trace and outcome calls under a bounded budget), so the brief overlap of a rolling update is harmless: the old replica releases the lease on its graceful stop and the new one acquires it within a renew interval. A steadily larger tier, though, refuses most node calls and only slows hand-outs, which is why there is no autoscaler on this Deployment. Token validation is stateless (shared signing key) and the scheduler and managed sync claim work by compare-and-swap, so the single replica is a dispatch decision, not a correctness limit on the API. Key environment:

- `ControlPlane__Worker__Enabled=false`: compute is the worker deployments' job.
- `ControlPlane__Proxy__Enabled=true` plus `ControlPlane__Proxy__KnownNetworks__0` set to your cluster's ingress/pod CIDR: `X-Forwarded-For`/`X-Forwarded-Proto` are honored only from the listed proxies, so clients cannot spoof their address, and rate limiting and login throttling key on the real client instead of the ingress IP. `ControlPlaneOptions.Validate()` refuses to start when proxy support is enabled but no `KnownNetworks` or `KnownProxies` are set, and rejects malformed CIDRs and IPs by name.
- Liveness probes `/health/live` (no dependencies); readiness probes `/health/ready` (catalog reachability).

### Workers: one Deployment plus one KEDA ScaledObject per pool

`deploy/k8s/worker-pool.yaml` is the template for one pool. Copy it per pool, setting `SQLFLOW_WORKER_POOL` in the Deployment and `?pool=<name>` in the ScaledObject's URL. KEDA's `metrics-api` scaler reads the pool's replica target from the control plane itself, `GET /api/v1/node/scale-target?pool=<name>` (node scope), and holds the Deployment at exactly that number; the scaler presents the same node token the workers use, through a bearer `TriggerAuthentication`, so no catalog credential exists on the compute tier at all:

```yaml
apiVersion: keda.sh/v1alpha1
kind: TriggerAuthentication
metadata:
  name: sqlflow-node-auth
  namespace: sqlflow
spec:
  secretTargetRef:
    - parameter: token
      name: sqlflow-secrets
      key: node-token
---
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: sqlflow-worker-default
  namespace: sqlflow
spec:
  scaleTargetRef:
    name: sqlflow-worker-default
  minReplicaCount: 0   # scale to zero when nothing is eligible and no node is busy
  maxReplicaCount: 10
  cooldownPeriod: 300  # keep a warm worker for five minutes after the work drains
  pollingInterval: 15
  triggers:
    - type: metrics-api
      metadata:
        url: "http://sqlflow-controlplane/api/v1/node/scale-target?pool="
        valueLocation: "replicas"
        targetValue: "1"
        authMode: "bearer"
      authenticationRef:
        name: sqlflow-node-auth
```

The target is the greatest of three terms (src/SqlFlow.Catalog/ScaleTargetStore.cs): the demand, the pool's always-on floor, and an active manual override, so the GUI's fleet controls steer the pool without the control plane ever calling the orchestrator. Demand is the pool's eligible queued runs (a member behind a lower wave, a group at its cap, or a run of a pipeline already executing asks for no replica, because no node could take it) divided by what one node of the pool executes at once (the nodes report it on every poll, so nothing in the manifest has to match a constant in the node runtime), plus the nodes currently busy (a queued-only count reads zero the moment the fleet takes a batch, which would let KEDA scale in mid-execution and kill pods carrying live runs). It is computed from the catalog journal, so every control-plane replica answers the same number.

`minReplicaCount: 0` means an idle pool costs nothing. Because runs are pinned to the repo's synced commit at enqueue, a cold-started worker needs only its environment: it polls the control plane, stages the snapshotted YAML (or materializes the pinned commit from git), executes, and reports back. The worker Deployment reaches the control plane Service and mounts one env entry per `${env:...}` connection reference the pool's flows use:

```yaml
env:
  - name: SQLFLOW_URL
    value: http://sqlflow-controlplane
  - name: SQLFLOW_TOKEN
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: node-token }
  - name: SQLFLOW_WORKER_POOL
    value: ""          # empty = untargeted runs only; set the pool name(s) for a pooled copy
  - name: SQLFLOW_GIT_TOKEN
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: git-token }
  - name: SQLFlowSinkConStr
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: sink-connection }
```

The template sets `terminationGracePeriodSeconds: 600` so an in-flight run can finish on scale-down; an interrupted run is requeued anyway.

### Placement

Workers must run where they can reach the data their pool's flows touch; that is the point of pools. A cloud cluster's workers serve cloud-reachable sources. An on-prem pool means workers running on-prem (compose, systemd, or a local cluster) pointed at the same catalog database and registered under that pool name.

## Azure Container Apps: the full estate with Bicep

`deploy/bicep/main.bicep` deploys everything into one resource group: Log Analytics plus the Container Apps environment, a Key Vault holding every secret, an Azure SQL catalog database, and the three apps composed from one template per tier. Each tier template (`control-plane.bicep`, `worker.bicep`, `gui.bicep`) also deploys standalone into an existing environment and Key Vault, which is how a second worker pool is added.

```bash
az group create -n sqlflow -l <region>
az acr create -g sqlflow -n <registry> --sku Basic
az acr build -r <registry> -t sqlflow-control-plane:latest .
az acr build -r <registry> -t sqlflow-worker:latest -f Dockerfile.worker .
az acr build -r <registry> -t sqlflow-gui:latest gui/

az deployment group create -g sqlflow -f deploy/bicep/main.bicep \
  -p acrName=<registry> \
     controlPlaneImage=<registry>.azurecr.io/sqlflow-control-plane:latest \
     workerImage=<registry>.azurecr.io/sqlflow-worker:latest \
     guiImage=<registry>.azurecr.io/sqlflow-gui:latest \
     sqlAdminPassword='<complex password>' \
     jwtSigningKey="$(openssl rand -base64 48)" \
     adminPassword='<initial admin password, 12+ chars>'
```

First start behaves exactly like compose: bootstrap provisioning applies the catalog migrations and creates the initial admin (`adminUsername`/`adminPassword`), so the `guiUrl` output is sign-in ready. The other outputs: `controlPlaneBaseUrl` (for the ADF pipeline below and CLI remotes), the control plane and worker identity client ids (grant them access to the data and secrets flows touch), `sqlServerFqdn`, and `keyVaultUri`.

How the Kubernetes layout maps onto Container Apps:

- **Two origins instead of a path split.** Every Container App has its own ingress FQDN, so `main.bicep` computes both hostnames up front, points the GUI's `SQLFLOW_API_BASE_URL` at the control plane URL, and CORS-lists the GUI origin on the control plane (`corsAllowedOrigins`). Restoring the one-host layout means putting Front Door or Application Gateway in front of both apps, then blanking both settings.
- **The control plane runs API-only and as one replica** (`workerEnabled: false` in the module call; `controlPlaneMaxReplicas` defaults to 1, because dispatch has one owner and a second replica would only refuse node calls); compute belongs to the worker app, exactly as in the k8s split.
- **KEDA is built in.** `worker.bicep` scales `0..maxReplicas` on the same scale-target endpoint as `worker-pool.yaml` (`?pool=` set from the `pool` parameter), a custom `metrics-api` rule authenticated with the Key Vault backed node token the container also presents, and keeps `terminationGracePeriodSeconds: 600` so an in-flight run can finish on scale-in. The worker app holds no catalog secret at all; without a node token it is deployed unscaled and takes no work until one is added.
- **Secrets live in Key Vault, read by user-assigned managed identity.** The deployment writes them, and each app identity gets Key Vault Secrets User plus AcrPull when `acrName` names a same-group registry. The deploying principal therefore needs to create role assignments (Owner or User Access Administrator) and to write vault secrets (Key Vault Secrets Officer, since the vault uses RBAC authorization). The same identities resolve `${keyvault:...}` references at run time (`SQLFLOW_AZURE_AUTH=mi`, `AZURE_CLIENT_ID`). The three estate databases are wired for free: flows reach staging and the warehouse as `${env:SQLFLOW_CONN_PRE}` and `${env:SQLFLOW_CONN_DWH}`, fixed names in every estate, so a document moves from test to prod unchanged (only the secrets' values differ). Data-source references land on the worker only, via `workerFlowEnv`: one `{ name, secretName }` entry per reference, naming an existing vault secret to wire to that environment variable. Data-source credentials are created in the vault out of band and never pass through the template.
- **The catalog is an Azure SQL database** (`sqlDatabaseSku`, default S1: the catalog is metadata plus the run queue, but it is polled continuously, so serverless auto-pause is the wrong shape). The ADO.NET connection string exists only as the `sqlflow-catalog-db` vault secret. The server admits Azure-service traffic (the consumption plan has no fixed egress address for a narrower rule); the hardening path is a VNet-integrated environment with a private endpoint to SQL and least-privilege or Entra credentials in place of the SQL admin, all behind that one secret.
- **Proxy trust stays off by default.** Container Apps ingress terminates TLS in front of the app, and the platform's forwarding hops have no contractual CIDR on the consumption plan; per-client rate limiting therefore keys on the ingress hop. For a VNet-integrated environment whose infrastructure subnet is known, pass it as `proxyKnownNetworks` on `control-plane.bicep` to key on real client addresses.
- **Bring your own SQL and network.** `existingSqlServer=<host[,port]>` skips the Azure SQL server and points the catalog connection at a server you already run; an address without a port gets `,1433` (a Managed Instance public endpoint would be `host,3342`). Bootstrap creates the catalog database on first start, so the login must be allowed to `CREATE DATABASE`. `infrastructureSubnetId` VNet-integrates the environment (consumption architecture: an undelegated subnet of at least /23), the route to a VNet-only Managed Instance: its default FQDN resolves to the private ILB address, and the default `AllowVnetInBound` rule admits 1433 unless a custom NSG rule denies it.
- **Optional AI Foundry.** `aiFoundryName` deploys an Azure AI Foundry account and project (`ai-foundry.bicep`) beside the estate and grants the control plane and worker identities Cognitive Services User, so anything they run can call deployed models keylessly via Entra. No model deployment is pinned in the template; nothing in the estate depends on the resource otherwise.

## Azure: ADF integration

SQLFlow runs inside ADF pipelines as a thin trigger, not as a container ADF boots per run. The model is an always-on control plane that a small ADF pipeline of Web Activities calls: authenticate, trigger, poll, fail on failure. Nothing is provisioned per run; a trigger is a sub-second authenticated call. The steps below deploy the single-app mode into an existing environment. On the full estate above the image, app, and migrations are already in place: register your repos (the second post-deploy note in step 2) and continue at step 3 with the estate's `controlPlaneBaseUrl` output.

### 1. Build and push the image

```bash
docker build -t <registry>.azurecr.io/sqlflow-control-plane:latest .
az acr login -n <registry>
docker push <registry>.azurecr.io/sqlflow-control-plane:latest
```

The image installs `libssl3` for LibGit2Sharp's SHA-pinned git materialization.

### 2. Deploy the control plane as a Container App

Put the two secrets in Key Vault first:

```bash
az keyvault secret set --vault-name <kv> --name sqlflow-catalog-db      --value "<ADO.NET connection string>"
az keyvault secret set --vault-name <kv> --name sqlflow-jwt-signing-key --value "<a random string of >= 32 bytes>"
```

Then deploy `deploy/bicep/control-plane.bicep`:

```bash
az deployment group create -g <rg> -f deploy/bicep/control-plane.bicep \
  -p managedEnvironmentId=<container-apps-env-id> \
     image=<registry>.azurecr.io/sqlflow-control-plane:latest \
     keyVaultName=<kv> \
     acrLoginServer=<registry>.azurecr.io
```

The template creates a user-assigned managed identity, grants it the Key Vault Secrets User role on the vault, and wires the app to pull the secrets at startup; no secret value appears in the template or app configuration. That same identity resolves `${keyvault:...}` references and cloud-storage credentials at run time (the template sets `SQLFLOW_AZURE_AUTH=mi` and `AZURE_CLIENT_ID`). The catalog connection lands in `SQLFLOW_CATALOG_DB`, which the control plane's default `ControlPlane:Catalog:ConnectionReference` (`${env:SQLFLOW_CATALOG_DB}`) reads directly. Probes hit `/health/live` and `/health/ready`; `minReplicas` defaults to 1 so a trigger never waits on a cold start.

Outputs:

- `controlPlaneBaseUrl`: pass to the ADF pipeline.
- `identityClientId`: grant it access to the SQL databases the flows touch and any flow secrets, since the control plane's worker resolves every credential from its own environment.

After deploy:

- Run the catalog migration once against the catalog connection: `sqlflow db migrate --db "<connection>"` (the `db` subcommands are `migrate|sync|status`; see `src/SqlFlow.Cli/Program.cs`).
- Register git repos as managed sources via `POST /api/v1/repos/sources` (body: `name`, `remoteUrl`, optional `branch` defaulting to `main`, optional `syncIntervalSeconds` defaulting to 300, optional `enabled` defaulting to true) so the catalog stays synced from git. A credential to pull a private remote is never accepted on this endpoint; the control plane resolves it from its own environment.

### 3. Import the ADF pipeline

Import `deploy/adf/SqlFlowTriggerFlow.pipeline.json` in ADF Studio (Author, Pipelines, Import from pipeline template) or deploy it as a `Microsoft.DataFactory/factories/pipelines` resource. Parameters:

| Parameter | Value |
|---|---|
| `controlPlaneBaseUrl` | the deploy output, e.g. `https://sqlflow-control-plane.<region>.azurecontainerapps.io` |
| `repoId` | the repo id from `GET /api/v1/repos` |
| `flowName` | the flow to run |
| `bootstrapSecret` | the control plane's bootstrap secret (secure string) |
| `pollIntervalSeconds` | poll cadence, default 15 |
| `timeout` | max wait as `d.hh:mm:ss`, default `1.00:00:00` |

The pipeline's activities:

1. `GetToken`: `POST /api/v1/auth/token` with the bootstrap secret and the `operate` scope.
2. `TriggerRun`: `POST /api/v1/runs` with `repoId` and `flowName`, bearer-authenticated.
3. `WaitForRun`: an Until loop that polls `GET /api/v1/runs/{id}` every `pollIntervalSeconds` until the status is `succeeded`, `failed`, or `cancelled`.
4. `FailIfRunDidNotSucceed`: fails the ADF pipeline with error code `SqlFlowRunFailed` when the terminal status is not `succeeded`, so a failed flow surfaces as a failed ADF pipeline.

### Routing and production notes

- **Pool routing**: add `"pool": "<pool>"` to the `TriggerRun` body to route the run to a node serving that pool; omit it for any node.
- **Version pinning**: add `"commitSha": "<sha>"` to run an exact committed version. The value must be a 4 to 64 character hexadecimal git object id; otherwise the API returns 400 with `commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit).` (`src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs`). Omitting it pins the run to the repo's last synced commit.
- **Token lifetime**: the pipeline fetches one token up front, and a bootstrap token does not renew (only an interactive sign-in rolls). If a flow can outlive `ControlPlane:Jwt:AccessTokenMinutes` (default 720, valid range 1 to 1440), raise that value or move `GetToken` inside the polling loop.
- **Secrets in ADF**: in production, source `bootstrapSecret` from a Key Vault linked service rather than a parameter value.

## Local source databases for development

`docker/` (distinct from `deploy/compose/`) spins up PostgreSQL, MySQL, and Oracle source containers seeded with the Sakila sample database for exploratory pipeline work and the foreign-source integration tests; see `docker/README.md`. These are source systems only; every flow writes to a SQL Server sink you run separately.

## See also

- [Control plane](../concepts/control-plane.md)
- [Worker command](../cli/worker.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
