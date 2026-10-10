// Deploys the isolated Power BI extractor (Dockerfile.pbix-extractor) as a Container App: the only place an uploaded
// .pbix is parsed. The control plane forwards an upload here and validates the specification that comes back.
//
// Isolation, as far as Container Apps expresses it:
//   - ingress is INTERNAL only (reachable inside the managed environment, never from the internet),
//   - the app's identity is granted nothing but the one Key Vault secret it reads (the shared key) and image pull,
//   - it holds no catalog, warehouse, or git credential, so a hostile report that compromises the process finds
//     nothing to use.
// Container Apps does not offer a read-only root filesystem or per-app egress rules; where outbound traffic must be
// blocked too, route the environment's subnet through a firewall/NSG that denies this app's egress.
//
//   az deployment group create -g <rg> -f pbix-extractor.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-pbix-extractor:latest keyVaultName=<vault> \
//        apiKeySecretName=sqlflow-pbix-extractor-key

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App.')
param name string = 'sqlflow-pbix-extractor'

@description('Resource id of an existing Container Apps managed environment to host the app (the control plane\'s).')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-pbix-extractor:latest')
param image string

@description('Name of the Key Vault holding the shared key.')
param keyVaultName string

@description('Name of the vault secret holding the shared key (at least 32 bytes); the control plane reads the same secret.')
param apiKeySecretName string = 'sqlflow-pbix-extractor-key'

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Largest report accepted, in megabytes. Keep it equal to the control plane\'s ReportExtraction:MaxUploadMegabytes.')
@minValue(1)
@maxValue(1024)
param maxUploadMegabytes int = 256

@description('Reports extracted at once per replica. Decompressing a model is memory-hungry; size memory to this.')
@minValue(1)
@maxValue(16)
param maxConcurrent int = 2

@description('vCPU per replica.')
param cpu string = '1.0'

@description('Memory per replica, paired with cpu. A large model decompresses to several times its file size.')
param memory string = '2Gi'

@description('Maximum replicas; each serves maxConcurrent extractions.')
@minValue(1)
param maxReplicas int = 2

// The Key Vault Secrets User built-in role, so the app's identity can read the shared key.
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

resource apiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: keyVault
  name: apiKeySecretName
}

// Scoped to the one secret rather than the vault, so this identity can read nothing else the estate keeps there.
resource keyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: apiKeySecret
  name: guid(apiKeySecret.id, identity.id, keyVaultSecretsUserRoleId)
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

var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

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
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'extractor-key'
          keyVaultUrl: apiKeySecret.properties.secretUri
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'pbix-extractor'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: [
            {
              name: 'PbixExtractor__ApiKey'
              secretRef: 'extractor-key'
            }
            {
              name: 'PbixExtractor__MaxUploadMegabytes'
              value: string(maxUploadMegabytes)
            }
            {
              name: 'PbixExtractor__MaxConcurrent'
              value: string(maxConcurrent)
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              // Not ready until the pbix-extract binary is present in the image.
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        // Uploads are rare and a cold start only delays one of them, but a warm replica keeps the GUI responsive.
        minReplicas: 1
        maxReplicas: maxReplicas
      }
    }
  }
  dependsOn: [
    keyAccess
    acrPull
  ]
}

@description('The address the control plane reaches the extractor at (ControlPlane:PowerAI:ReportExtraction:Endpoint): the environment-internal FQDN.')
output endpoint string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('The principal (object) id of the app identity.')
output identityPrincipalId string = identity.properties.principalId
