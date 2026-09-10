// Deploys one SQLFlow worker pool as a Container App: `sqlflow worker`, the pull-based drain loop over the
// durable run queue. Workers expose nothing (no ingress; outbound SQL to the catalog plus outbound git for
// SHA-pinned materialization) and scale 0..N on QUEUE DEPTH through the built-in KEDA mssql scaler, so an idle
// pool costs nothing. Because runs are pinned to the repo's synced commit at enqueue, a cold-started replica
// needs only its environment: it claims, materializes the pinned commit, executes, and reports back.
//
// This is the Container Apps mirror of deploy/k8s/worker-pool.yaml: deploy one copy per pool (set name and
// pool together). Secrets come from an existing Key Vault, read by the app's user-assigned managed identity;
// that same identity resolves ${keyvault:...} references at run time (SQLFLOW_AZURE_AUTH=mi).
//
//   az deployment group create -g <rg> -f worker.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-worker:latest keyVaultName=<kv>

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity). Deploy one app per pool, e.g. sqlflow-worker-etl.')
param name string = 'sqlflow-worker'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-worker:latest')
param image string

@description('Name of an existing Key Vault holding the catalog connection (and any flow) secrets.')
param keyVaultName string

@description('Key Vault secret name for the catalog ADO.NET connection string.')
param catalogConnectionSecretName string = 'sqlflow-catalog-db'

@description('The pool this deployment serves (single pool name, used for both SQLFLOW_WORKER_POOL and the scale query). Empty drains untargeted runs only.')
param pool string = ''

@description('Key Vault secret name for a token for private git remotes (SHA-pinned materialization). Leave empty when remotes are public.')
param gitTokenSecretName string = ''

@description('Username paired with the git token when the host requires one (Bitbucket app passwords take the account username, repository access tokens take x-token-auth; GitHub ignores it). Empty sends the token alone.')
param gitUsername string = ''

@description('Flow environment references, one object per \${env:...} reference the pool\'s flows use: { name: the environment variable, secretName: the Key Vault secret holding its value }. Credentials live on the node, never in the control plane.')
param flowEnv array = []

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Key Vault secret name for a Go-driver-compatible catalog connection string used ONLY by the KEDA scale rule (a go-mssqldb URL, sqlserver://user:urlencoded-pw@host:port?database=...). The KEDA mssql scaler is not .NET SqlClient: it does not strip the single quotes an ADO.NET connection string puts around a password, so the .NET catalog connection cannot be reused for the scaler. Leave empty to reuse catalogConnectionSecretName for the scaler (correct only when that password needs no quoting).')
param scalerConnectionSecretName string = ''

@description('Upper bound for queue-depth scale out.')
@minValue(1)
param maxReplicas int = 10

@description('How many runs one replica executes at once. MUST match the node\'s own concurrency (SqlFlow.Node RunWorker.DefaultMaxConcurrentRuns, 4), because the scale rule divides queued work by it to size the fleet. Setting it higher than the node\'s value starves the queue; lower spawns replicas that find nothing left to claim.')
@minValue(1)
param maxConcurrentRunsPerReplica int = 4

@description('vCPU per replica, as a string for exact decimals. Must form a valid Container Apps consumption pair with memory.')
param cpu string = '1.0'

@description('Memory per replica, paired with cpu.')
param memory string = '2.0Gi'

// The Key Vault Secrets User built-in role, so the app's identity can read the configured secrets.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
// The AcrPull built-in role, for managed-identity image pull from a same-group registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource keyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Only referenced when acrName is set; the placeholder name is never resolved (ARM evaluates the branch lazily).
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: empty(acrName) ? 'unused' : acrName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrName)) {
  scope: acr
  name: guid(resourceGroup().id, acrName, identity.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Constructed from the vault name (not read from the resource) so the flowEnvSecrets loop below stays
// computable at the start of the deployment, which Bicep requires of for-bodies in variables.
var vaultUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'
var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

// Secrets are pulled from Key Vault by the app's managed identity; their values never appear here. The KEDA
// scaler reads the same Key Vault backed secret for its connection.
var baseSecrets = [
  {
    name: 'catalog-db'
    keyVaultUrl: '${vaultUri}secrets/${catalogConnectionSecretName}'
    identity: identity.id
  }
]

var gitTokenSecrets = empty(gitTokenSecretName) ? [] : [
  {
    name: 'git-token'
    keyVaultUrl: '${vaultUri}secrets/${gitTokenSecretName}'
    identity: identity.id
  }
]

var flowEnvSecrets = [for (entry, i) in flowEnv: {
  name: 'flow-env-${i}'
  keyVaultUrl: '${vaultUri}secrets/${entry.secretName}'
  identity: identity.id
}]

// A distinct secret for the KEDA scale rule when the scaler needs a different (Go-driver) connection string
// than the .NET container. When unset, the scaler falls back to the container's catalog-db secret.
var scalerSecrets = empty(scalerConnectionSecretName) ? [] : [
  {
    name: 'catalog-scaler'
    keyVaultUrl: '${vaultUri}secrets/${scalerConnectionSecretName}'
    identity: identity.id
  }
]
var scalerSecretRef = empty(scalerConnectionSecretName) ? 'catalog-db' : 'catalog-scaler'

var baseEnv = [
  {
    name: 'SQLFLOW_CATALOG_DB'
    secretRef: 'catalog-db'
  }
  // Empty = untargeted runs only, matching the scale query below.
  {
    name: 'SQLFLOW_WORKER_POOL'
    value: pool
  }
  // Resolve ${keyvault:...}/cloud-storage credentials at run time via this managed identity.
  {
    name: 'SQLFLOW_AZURE_AUTH'
    value: 'mi'
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
]

var gitTokenEnv = empty(gitTokenSecretName) ? [] : [
  {
    name: 'SQLFLOW_GIT_TOKEN'
    secretRef: 'git-token'
  }
]

var gitUsernameEnv = empty(gitUsername) ? [] : [
  {
    name: 'SQLFLOW_GIT_USERNAME'
    value: gitUsername
  }
]

// Every ${env:...} reference the pool's flows use resolves HERE, on the node.
var flowEnvVars = [for (entry, i) in flowEnv: {
  name: entry.name
  secretRef: 'flow-env-${i}'
}]

// The replica target is the GREATEST of demanded work, the always-on floor, and an active manual override, all read
// from the catalog, so the GUI's fleet controls steer scaling without the control plane ever calling the
// orchestrator (it only writes [catalog].[WorkerPool] rows; KEDA, which already queries the catalog, reads them).
// The same query shape as deploy/k8s/worker-pool.yaml. A pool with no WorkerPool row (ISNULL -> 0) still scales to
// zero once nothing is queued and no node is busy. REQUIRES the WorkerPool table AND [Node].[BusyRuns] (catalog
// migrations WorkerPoolDesiredAndNodeRestart + RunAttemptFencingAndNodeBusyRuns): deploy the control plane first
// so both migrations land, then this revision.
//
// The demanded-work term is queued runs PLUS busy nodes (nodes whose heartbeat reports BusyRuns > 0 within the
// 60s liveness window). Queued runs ask for capacity to start; busy nodes hold the capacity they occupy, so
// scale-in only ever reclaims idle replicas' worth of target. A queued-only count read zero the moment the fleet
// claimed the batch, which let KEDA scale in mid-execution and terminate pods carrying live runs. Counting nodes
// (not running runs) keeps the target honest when one node executes several runs at once, and the liveness window
// means a dead node's last busy count can never pin a replica: the orphan reaper requeues its runs, which
// re-enter the queued term until a live node claims them. The platform still picks scale-in victims blindly, so a
// busy pod can be condemned in the claim/scale-in race; terminationGracePeriodSeconds lets it drain, and the
// reaper's requeue makes even a severed run recoverable.
//
// The queued term is DIVIDED by maxConcurrentRunsPerReplica, because a replica is not worth one run: the node
// drains up to RunWorker.DefaultMaxConcurrentRuns (4) at once. Asking for one replica per queued run made a
// schedule wave spawn four times the fleet it needed, and the surplus pods found nothing left to claim by the
// time they had pulled the image and started polling: over one 24h window 79 of 150 pods executed zero runs and
// were reclaimed at the scaler's cooldown floor. Only the queued term is divided; busy nodes still count one
// each, since dividing occupied capacity would ask the platform to reclaim pods that are executing runs.
//
// That term also counts only CLAIMABLE runs, mirroring the three gates in RunQueueStore.ClaimSqlTemplate: a
// member of a wave-ordered group waits for every lower wave to go terminal, a member carrying a
// GroupMaxConcurrency waits for a free slot in its group, and a run whose pipeline is already executing waits its
// turn. A plain COUNT of queued rows asks for capacity no worker is permitted to take: one schedule fire enqueued
// 51 wave-ordered members at once, the group then sat queued for four hours behind its wave-1 acquisition, and
// the rule kept ordering replicas that started, claimed nothing, and died at the cooldown floor. Demand has to
// mean work a node could pick up right now. Keep these predicates in step with the claim statement; the scaler
// evaluates them over queued rows only, which is the same small set every node's claim poll already scans.
var queueDepthQuery = empty(pool)
  ? 'SELECT (SELECT MAX(v) FROM (VALUES ((CAST(CEILING((SELECT COUNT(*) FROM [catalog].[Run] AS r WHERE r.[Status] = \'queued\' AND r.[TargetPool] IS NULL AND (r.[GroupId] IS NULL OR NOT EXISTS (SELECT 1 FROM [catalog].[Run] AS s WHERE s.[GroupId] = r.[GroupId] AND s.[GroupWave] < r.[GroupWave] AND s.[Status] IN (\'queued\', \'running\'))) AND (r.[GroupMaxConcurrency] IS NULL OR (SELECT COUNT(*) FROM [catalog].[Run] AS w WHERE w.[GroupId] = r.[GroupId] AND w.[Status] = \'running\') < r.[GroupMaxConcurrency]) AND NOT EXISTS (SELECT 1 FROM [catalog].[Run] AS p WHERE p.[PipelineId] = r.[PipelineId] AND p.[Status] = \'running\')) / ${maxConcurrentRunsPerReplica}.0) AS int) + (SELECT COUNT(*) FROM [catalog].[Node] WHERE [BusyRuns] > 0 AND [LastSeenUtc] >= DATEADD(second, -60, SYSUTCDATETIME()) AND ([Pool] = N\'\' OR [Pool] IS NULL)))), (ISNULL((SELECT [MinReplicas] FROM [catalog].[WorkerPool] WHERE [Pool] = N\'\'), 0)), (ISNULL((SELECT CASE WHEN [ManualUntilUtc] > SYSUTCDATETIME() THEN [ManualReplicas] ELSE 0 END FROM [catalog].[WorkerPool] WHERE [Pool] = N\'\'), 0))) AS t(v))'
  : 'SELECT (SELECT MAX(v) FROM (VALUES ((CAST(CEILING((SELECT COUNT(*) FROM [catalog].[Run] AS r WHERE r.[Status] = \'queued\' AND r.[TargetPool] = \'${pool}\' AND (r.[GroupId] IS NULL OR NOT EXISTS (SELECT 1 FROM [catalog].[Run] AS s WHERE s.[GroupId] = r.[GroupId] AND s.[GroupWave] < r.[GroupWave] AND s.[Status] IN (\'queued\', \'running\'))) AND (r.[GroupMaxConcurrency] IS NULL OR (SELECT COUNT(*) FROM [catalog].[Run] AS w WHERE w.[GroupId] = r.[GroupId] AND w.[Status] = \'running\') < r.[GroupMaxConcurrency]) AND NOT EXISTS (SELECT 1 FROM [catalog].[Run] AS p WHERE p.[PipelineId] = r.[PipelineId] AND p.[Status] = \'running\')) / ${maxConcurrentRunsPerReplica}.0) AS int) + (SELECT COUNT(*) FROM [catalog].[Node] WHERE [BusyRuns] > 0 AND [LastSeenUtc] >= DATEADD(second, -60, SYSUTCDATETIME()) AND [Pool] = N\'${pool}\'))), (ISNULL((SELECT [MinReplicas] FROM [catalog].[WorkerPool] WHERE [Pool] = N\'${pool}\'), 0)), (ISNULL((SELECT CASE WHEN [ManualUntilUtc] > SYSUTCDATETIME() THEN [ManualReplicas] ELSE 0 END FROM [catalog].[WorkerPool] WHERE [Pool] = N\'${pool}\'), 0))) AS t(v))'

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress: the worker has no inbound surface at all.
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: concat(baseSecrets, gitTokenSecrets, flowEnvSecrets, scalerSecrets)
    }
    template: {
      // Let an in-flight run finish on scale-in or revision swap; an interrupted one is requeued anyway.
      terminationGracePeriodSeconds: 600
      containers: [
        {
          name: 'worker'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(baseEnv, gitTokenEnv, gitUsernameEnv, flowEnvVars)
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'queue-depth'
            custom: {
              type: 'mssql'
              metadata: {
                query: queueDepthQuery
                targetValue: '1'
              }
              auth: [
                {
                  secretRef: scalerSecretRef
                  triggerParameter: 'connectionString'
                }
              ]
            }
          }
        ]
      }
    }
  }
  // The app resolves its Key Vault secrets (and pulls the image) at creation, so the role grants must exist
  // first. ARM drops the acrPull entry when that resource's condition is false.
  dependsOn: [
    keyVaultAccess
    acrPull
  ]
}

@description('The client id of the worker identity (grant it access to the data its pool\'s flows touch).')
output identityClientId string = identity.properties.clientId

@description('The principal (object) id of the worker identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId
