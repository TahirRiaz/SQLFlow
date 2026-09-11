// Deploys one SQLFlow worker pool as a Container App: `sqlflow worker`, the pull-based drain loop over the
// control plane's dispatcher. Workers expose nothing (no ingress; outbound HTTPS to the control plane, outbound
// SQL to the data their flows touch, and outbound git for a pinned run without a snapshot) and scale 0..N on the
// control plane's own replica target through the built-in KEDA metrics-api scaler, so an idle pool costs nothing
// and no catalog credential reaches this tier at all. A cold-started replica needs only its environment: it
// polls, is handed a run with its definition, fetches the snapshotted YAML (or materializes the pinned commit),
// executes, streams the trace, and reports back.
//
// This is the Container Apps mirror of deploy/k8s/worker-pool.yaml: deploy one copy per pool (set name and
// pool together). Secrets come from an existing Key Vault, read by the app's user-assigned managed identity;
// that same identity resolves ${keyvault:...} references at run time (SQLFLOW_AZURE_AUTH=mi).
//
//   az deployment group create -g <rg> -f worker.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-worker:latest keyVaultName=<kv> \
//        controlPlaneUrl=https://<control-plane-fqdn> nodeTokenSecretName=sqlflow-node-token

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity). Deploy one app per pool, e.g. sqlflow-worker-etl.')
param name string = 'sqlflow-worker'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-worker:latest')
param image string

@description('Name of an existing Key Vault holding the node token (and any flow) secrets.')
param keyVaultName string

@description('The control plane base URL the node polls for work over the node protocol (https://<control-plane-fqdn>). Every call is outbound from the node, and the scale rule reads the pool\'s replica target from the same host.')
param controlPlaneUrl string

@description('Key Vault secret name holding a personal access token minted with the node scope, which the node presents to the control plane and the scale rule presents to the scale-target endpoint. Mint it after the control plane is up (an admin: POST /api/v1/me/tokens with scopes ["node"]) and store it under this name before deploying the worker; leave empty to deploy the worker without a credential (it then cannot take work, and it is not scaled, until one is added).')
param nodeTokenSecretName string = ''

@description('The pool this deployment serves (single pool name, used for both SQLFLOW_WORKER_POOL and the scale-target query). Empty drains untargeted runs only.')
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

@description('Upper bound for scale out.')
@minValue(1)
param maxReplicas int = 10

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

// Secrets are pulled from Key Vault by the app's managed identity; their values never appear here. The node token
// is the only credential this tier needs towards SQLFlow itself: the node presents it on every node-protocol call,
// and the scale rule presents it to the scale-target endpoint.
var nodeTokenSecrets = empty(nodeTokenSecretName) ? [] : [
  {
    name: 'node-token'
    keyVaultUrl: '${vaultUri}secrets/${nodeTokenSecretName}'
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

var baseEnv = [
  // The dispatcher the node polls for work; the run queue lives there, never in the catalog, and everything a
  // run needs (its definition, YAML, lineage context) and produces (its trace, its outcome) travels through it.
  {
    name: 'SQLFLOW_URL'
    value: controlPlaneUrl
  }
  // Empty = untargeted runs only, matching the scale-target query below.
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

var nodeTokenEnv = empty(nodeTokenSecretName) ? [] : [
  {
    name: 'SQLFLOW_TOKEN'
    secretRef: 'node-token'
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

// The replica target is answered by the control plane itself (GET /api/v1/node/scale-target?pool=..., node
// scope): the GREATEST of demanded work, the pool's always-on floor, and an active manual override, so the GUI's
// fleet controls steer scaling without the control plane ever calling the orchestrator. Demand is the pool's
// ELIGIBLE queued runs (those no gate holds back: a member behind a lower wave, a group at its cap, or a run of a
// pipeline that is already executing asks for no replica, because no node could take it) divided by what one node
// of the pool executes at once (reported by the nodes themselves on every poll, so nothing here has to match a
// constant in the node runtime), plus the nodes currently busy (a queued-only count reads zero the moment the fleet
// takes a batch, which would let the platform scale in mid-execution and kill replicas carrying live runs; busy
// nodes hold the capacity they occupy, so scale-in only ever reclaims idle replicas' worth of target). The
// endpoint is computed from the catalog journal, so every control-plane replica answers the same number and the
// rule never depends on which replica it reached. KEDA's metrics-api scaler reads `replicas` against a target of 1,
// so the fleet is held at exactly that number. The rule needs the node token; without one the app is deployed
// unscaled (and, with no credential, takes no work either) until the token is added.
var scaleRules = empty(nodeTokenSecretName) ? [] : [
  {
    name: 'replica-target'
    custom: {
      type: 'metrics-api'
      metadata: {
        url: '${controlPlaneUrl}/api/v1/node/scale-target?pool=${uriComponent(pool)}'
        valueLocation: 'replicas'
        targetValue: '1'
        authMode: 'bearer'
      }
      auth: [
        {
          secretRef: 'node-token'
          triggerParameter: 'token'
        }
      ]
    }
  }
]

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
      secrets: concat(nodeTokenSecrets, gitTokenSecrets, flowEnvSecrets)
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
          env: concat(baseEnv, nodeTokenEnv, gitTokenEnv, gitUsernameEnv, flowEnvVars)
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: maxReplicas
        rules: scaleRules
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
