# Deploying SQLFlow

Three core images, plus two optional ones for the Slack assistant:

| Image | Built from | Scales on | Behind the ingress? |
|---|---|---|---|
| `sqlflow-control-plane` | `Dockerfile` | request load (HPA) | yes, under `/api` |
| `sqlflow-gui` | `gui/Dockerfile` | trivially (static) | yes, under `/` |
| `sqlflow-worker` | `Dockerfile.worker` | queue depth (KEDA) | never (pull model: it polls the control plane for work, no inbound surface) |
| `sqlflow-mcp` (optional) | `Dockerfile.mcp` | pinned to 1 (in-memory MCP sessions) | own ingress, bearer-gated `/mcp` |
| `sqlflow-slack-bot` (optional) | `Dockerfile.slackbot` | pinned to 1 (Socket Mode dials out) | never |

```bash
docker build -t sqlflow-control-plane:latest .
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker build -t sqlflow-gui:latest gui/
docker build -f Dockerfile.mcp -t sqlflow-mcp:latest .
docker build -f Dockerfile.slackbot -t sqlflow-slack-bot:latest .
```

## Local / single host: docker compose

```bash
cd deploy/compose
cp .env.example .env    # set the secrets
docker compose up -d --build
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

Workers take work from the control plane's dispatcher over HTTP, authenticated with a personal access token
minted with the `node` scope. That token can only be minted once the control plane is running, so a first
start is two steps: bring up everything but the worker, sign in as the admin and mint the token
(`POST /api/v1/me/tokens` with scopes `["node"]`, or the GUI's token page), set it as `SQLFLOW_NODE_TOKEN` in
`.env`, then start the worker:

```bash
docker compose up -d mssql controlplane gui
# mint the node token, put it in .env as SQLFLOW_NODE_TOKEN
docker compose up -d worker
```

GUI at http://localhost:8081, API at http://localhost:5000. Bootstrap provisioning creates the catalog database,
applies migrations, seeds roles, and creates the admin from `.env` on first start.

## Azure Container Apps: `deploy/bicep/`

The same three-tier layout on managed infrastructure, one template per tier plus a composition:

| Template | Deploys |
|---|---|
| `main.bicep` | The full estate: Log Analytics + the Container Apps environment, a Key Vault holding every secret, an Azure SQL catalog database, and the three apps below. |
| `control-plane.bicep` | The API as an always-on Container App. Standalone it is the single-app mode ADF triggers; `main.bicep` runs it API-only. |
| `worker.bicep` | One worker pool: no ingress, scaled 0..N on queue depth by the built-in KEDA mssql scaler. One deployment per pool. |
| `gui.bicep` | The SPA behind its own ingress. |
| `ai-foundry.bicep` | Optional: an Azure AI Foundry account + project beside the estate (`aiFoundryName` on `main.bicep`), the app identities granted keyless caller access, plus an optional pinned model deployment (`aiFoundryModelName`) and Responses API access (Cognitive Services OpenAI User) for the Slack bot identity. |
| `mcp.bicep` | Optional: the SQLFlow MCP server in HTTP mode (`mcpImage` on `main.bicep`), the tool source for the Foundry agent and any remote MCP client. Holds no credentials; every `/mcp` request must present a SQLFlow bearer token, which it forwards to the control plane. |
| `slack-bot.bicep` | Optional: the Slack assistant (`slackBotImage` on `main.bicep`), a Socket Mode relay to a Foundry agent whose tools are the MCP server. No ingress; secrets from Key Vault via managed identity. |

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

Sign in at the `guiUrl` output with the bootstrap admin (first start applies the catalog migrations and creates
the user); point ADF at `controlPlaneBaseUrl` (see `deploy/adf`). How the k8s layout maps onto Container Apps:

- **Two origins instead of a path split**: every Container App has its own ingress FQDN, so `main.bicep` wires
  the GUI's `SQLFLOW_API_BASE_URL` to the control plane URL and CORS-lists the GUI origin on the control plane.
  Put Front Door or Application Gateway in front of both apps to restore the one-host layout, then blank both.
- **KEDA is built in**: `worker.bicep` runs the same mssql queue-depth query as `worker-pool.yaml` with
  `minReplicas: 0`. Add a pool with another deployment of it (`-p name=sqlflow-worker-<pool> pool=<pool>`).
- **Secrets live in Key Vault, read by managed identity**: no secret value appears in the templates or app
  configuration, and the same identities resolve `${keyvault:...}` references at run time
  (`SQLFLOW_AZURE_AUTH=mi`). The three estate databases are wired for free: flows reach staging and the
  warehouse as `${env:SQLFLOW_CONN_PRE}` and `${env:SQLFLOW_CONN_DWH}`, fixed names in every estate, so a
  document moves from test to prod unchanged. Data-source references are added via `workerFlowEnv`, one
  `{ name, secretName }` entry per reference naming a secret created in the vault out of band, so no
  data-source credential passes through the template. The deploying principal needs to create role assignments
  (Owner or User Access Administrator) and to write vault secrets (Key Vault Secrets Officer; the vault uses RBAC).
- **Catalog on Azure SQL**: the connection string (SQL auth) exists only as the `sqlflow-catalog-db` vault
  secret; the server allows Azure-service traffic because consumption-plan apps have no fixed egress address.
  Hardening path: a VNet-integrated environment with a private endpoint to SQL, and least-privilege or Entra
  identities in place of the SQL admin. App config never changes; update the one secret.
- **Bring your own SQL and network**: `existingSqlServer=<host[,port]>` points the catalog at a server you
  already run (a Managed Instance FQDN, for example) instead of creating one; bootstrap creates the database on
  first start. `infrastructureSubnetId` VNet-integrates the environment (an undelegated /23), which is how the
  apps reach a VNet-only Managed Instance privately.
- **Private git remotes**: `gitToken` lands in Key Vault and reaches managed sync (control plane) and
  materialization (workers) as `SQLFLOW_GIT_TOKEN`. Hosts that pair the token with a username (Bitbucket app
  passwords or `x-token-auth` repository tokens) also set `gitUsername`; GitHub needs the token alone.

## The Slack assistant: `deploy/slack/` + `mcp.bicep` + `slack-bot.bicep`

Ask SQLFlow questions from Slack ("what failed last night?", "what feeds `dbo.Orders`?"). The chain is:

```
Slack thread -> sqlflow-slack-bot (Socket Mode relay) -> the model provider (one of three modes)
            -> sqlflow-mcp over streamable HTTP (tools) -> control plane /api/v1 (bearer-scoped)
```

The bot supports three provider modes (`slackBotProvider`); all three use the same SQLFlow MCP server, the
same instructions, and the same read-only tool allowlist, so the Slack experience is identical:

| Mode | Model host | Credential | Azure AI dependency |
|---|---|---|---|
| `AzureFoundry` (default) | Azure AI Foundry, Responses API | The bot's managed identity (no key) | Yes: the Foundry account + model deployment |
| `OpenAI` | api.openai.com, the same Responses API + MCP tool wire format | An OpenAI API key (`sk-...`) | None |
| `Anthropic` | The Claude API (Messages API with the MCP connector) | An Anthropic API key (`sk-ant-...`) | None |

In the Responses-API modes (`AzureFoundry`, `OpenAI`) each question is one call carrying the model, the
instructions, and the MCP tool (server URL, allowlist, and the `Authorization` header); a Slack thread's
follow-ups chain server-side via `previous_response_id`, and the model must be Responses-API + MCP-capable
(for example `gpt-5-mini`). In the `Anthropic` mode the Claude MCP connector calls the same MCP server
server-side; Claude keeps no server-side conversation state, so the bot replays the Slack thread transcript
each turn (the Slack thread is the durable record either way). The bot's whole SQLFlow authority is one
read-scoped personal access token, sent to the MCP server as the `Authorization` header;
`trigger_run`/`cancel_run` are excluded from the tool allowlist. See the reference guide for the full contract.
IDE assistants (Copilot, Claude, Cursor) keep using `sqlflow-mcp` locally over stdio; this deploys the same
binary in `http` mode as a shared, remote tool source.

Setup, in order:

1. **Deploy the estate first** (previous section), if it is not already up.
2. **Create the Slack app**: at https://api.slack.com/apps choose "From an app manifest" and paste
   `deploy/slack/manifest.yaml`. Install it to the workspace, then collect the app-level token
   (`xapp-...`, generated under Basic Information with `connections:write`) and the bot token (`xoxb-...`,
   under OAuth & Permissions).
3. **Mint the bot's SQLFlow token**: in the GUI (or CLI), create a personal access token with the `read`
   scope only. This is the credential the agent presents to the MCP server on every run.
4. **Build and push the two images** (`az acr build -r <registry> -t sqlflow-mcp:latest -f Dockerfile.mcp .`
   and `-t sqlflow-slack-bot:latest -f Dockerfile.slackbot .`).
5. **Redeploy `main.bicep`** with the assistant parameters added. In `AzureFoundry` mode (the default):

```bash
az deployment group create -g sqlflow -f deploy/bicep/main.bicep \
  -p <the parameters from the estate deployment above> \
     aiFoundryName=<globally-unique-name> \
     aiFoundryModelName=gpt-5-mini \
     mcpImage=<registry>.azurecr.io/sqlflow-mcp:latest \
     slackBotImage=<registry>.azurecr.io/sqlflow-slack-bot:latest \
     slackAppToken='xapp-...' slackBotToken='xoxb-...' \
     slackBotSqlflowToken='sqlf_...'
```

Or without any Azure AI dependency, on a plain API key (skip `aiFoundryName`/`aiFoundryModelName` entirely):

```bash
# OpenAI platform key
     slackBotProvider=OpenAI slackBotModelApiKey='sk-...' slackBotOpenAIModel=gpt-5-mini
# or the Anthropic Claude API key
     slackBotProvider=Anthropic slackBotModelApiKey='sk-ant-...' slackBotAnthropicModel=claude-opus-4-8
```

In `AzureFoundry` mode this deploys the Foundry account + project + model deployment and grants the bot
identity the Cognitive Services OpenAI User role on the account (its Responses API access); in the key-based
modes no Foundry resources are touched and the provider key lands in Key Vault alongside the tokens. Then
invite the bot to a channel and mention it. Notes:

- **First question after a fresh AzureFoundry deployment can fail once** while the Cognitive Services OpenAI
  User role assignment propagates to the bot's managed identity; ask again and it recovers. The key-based
  modes have no role assignment and work immediately.
- **The MCP endpoint is public but bearer-gated**: the provider's runtime calls in from its own compute
  (Microsoft's for Foundry, OpenAI's or Anthropic's in the key-based modes), so `sqlflow-mcp` has external
  ingress; every `/mcp` request must carry a valid SQLFlow token or it is rejected at the edge, and all data
  access is enforced by the control plane per token scope. `/healthz` is the only unauthenticated route.
- **Everyone in the workspace shares the bot's read-only identity**. Widening the allowlist (for example
  adding `trigger_run`) means anyone in any channel the bot is in can fire it; do not, until per-user
  account linking exists.
- **Answers stay grounded in Slack**: the Slack thread is the durable transcript. The bot maps threads to
  Foundry threads in memory and rebuilds them from the Slack history after a restart, so context survives
  redeployments without any state store.

## Kubernetes: `deploy/k8s/`

Prerequisites: an ingress controller (the annotations assume ingress-nginx), cert-manager or another TLS source,
and [KEDA](https://keda.sh) for worker autoscaling.

```bash
kubectl apply -f deploy/k8s/namespace.yaml
# create the real secret (see secrets.example.yaml for the required keys), then:
kubectl apply -f deploy/k8s/controlplane.yaml
kubectl apply -f deploy/k8s/gui.yaml
kubectl apply -f deploy/k8s/ingress.yaml
kubectl apply -f deploy/k8s/worker-pool.yaml
```

The layout and the reasoning behind it:

- **One host, path split** (`/api` and `/openapi` to the control plane, `/` to the GUI): the SPA runs
  same-origin with the API, so no CORS configuration exists anywhere. The GUI image's
  `SQLFLOW_API_BASE_URL=""` means "same origin".
- **Control plane replicas are API-only** (`ControlPlane__Worker__Enabled=false`): the HTTP tier scales on
  request load via the HPA, independent of compute. Multi-replica is safe by construction: stateless JWT
  validation, and the scheduler / managed sync / run queue all claim work atomically in the catalog.
- **Forwarded headers are trusted from the ingress only** (`ControlPlane__Proxy__*`): set `KnownNetworks` to
  your cluster's ingress/pod CIDR. Without this, rate limiting and login throttling would key on the ingress
  address instead of the real client.
- **Workers scale 0 to N per pool on queue depth**: KEDA's mssql scaler counts queued runs for the pool and
  scales the matching worker Deployment; `minReplicaCount: 0` means an idle pool costs nothing. Copy
  `worker-pool.yaml` per pool (set `SQLFLOW_WORKER_POOL` and the query's `TargetPool` predicate). Because runs
  are pinned to the repo's synced commit at enqueue, a cold-started worker needs only its environment: it
  materializes the exact commit from git and executes.
- **Secrets stay on the tier that uses them**: the control plane gets the catalog connection and JWT material;
  workers get a node token, the git token and every `${env:...}` connection their pool's flows reference, and
  never the catalog connection (a node speaks only the node protocol; the KEDA scaler's own catalog credential is
  the one exception until the metrics scaler replaces it). Nothing data-plane ever passes through the control plane.

## Placement reminder

Workers must run where they can reach the data their pool's flows touch (that is the point of pools). A cloud
cluster's workers serve cloud-reachable sources; an on-prem pool means workers on-prem (compose, systemd, or a
local cluster) pointed at the same catalog database, registered under that pool name.
