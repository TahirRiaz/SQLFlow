// Deploys the SQLFlow GUI as a Container App: the built SPA served by unprivileged nginx. The API base URL is
// injected at container start (the image writes /config.json from SQLFLOW_API_BASE_URL), so one image serves
// every environment. On Container Apps each app has its own ingress FQDN, so the usual layout is two origins:
// point apiBaseUrl at the control plane URL and list this app's origin in the control plane's CORS
// (corsAllowedOrigins on control-plane.bicep). Behind a path-splitting front (Front Door, Application Gateway)
// pass an empty apiBaseUrl for the same-origin layout and drop the CORS entries.
//
//   az deployment group create -g <rg> -f gui.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-gui:latest apiBaseUrl=<control-plane-url>

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity).')
param name string = 'sqlflow-gui'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-gui:latest')
param image string

@description('The control plane origin the SPA calls, e.g. https://sqlflow-control-plane.<env>.azurecontainerapps.io. Empty means same-origin, valid only behind a path-splitting front.')
param apiBaseUrl string

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Minimum replicas. Static content: one is enough, more buys ingress resilience.')
@minValue(1)
param minReplicas int = 1

@description('Maximum replicas for ingress autoscale.')
param maxReplicas int = 3

// The AcrPull built-in role, for managed-identity image pull from a same-group registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
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
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'gui'
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'SQLFLOW_API_BASE_URL'
              value: apiBaseUrl
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/'
                port: 8080
              }
              periodSeconds: 15
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-load'
            http: {
              metadata: {
                concurrentRequests: '200'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPull
  ]
}

@description('The GUI URL: sign in here with the bootstrap admin.')
output guiUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
