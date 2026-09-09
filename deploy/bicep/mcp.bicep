// Deploys the SQLFlow MCP server in HTTP mode as a Container App: `sqlflow-mcp http`, the streamable HTTP
// endpoint remote MCP clients connect to. Azure AI Foundry's MCP tool (the Slack assistant's tool source) is
// the primary consumer, but any MCP client that can send an Authorization header works.
//
// The app holds NO credentials: every /mcp request must carry a SQLFlow bearer token, which the server
// forwards to the control plane per tool call, so authorization always happens where the data lives. Ingress
// is external because Foundry's agent runtime calls in from Microsoft-managed compute; the mandatory bearer
// plus TLS is the access control. /healthz serves the platform probes without auth.
//
//   az deployment group create -g <rg> -f mcp.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-mcp:latest controlPlaneUrl=https://... \
//        guiUrl=https://sqlflow-gui.<env-domain>

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App.')
param name string = 'sqlflow-mcp'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-mcp:latest')
param image string

@description('Base URL of the SQLFlow control plane the MCP tools proxy, e.g. https://sqlflow-control-plane.<env-domain>.')
param controlPlaneUrl string

@description('Public base URL of the SQLFlow GUI, e.g. https://sqlflow-gui.<env-domain>. Tool results carry deep links into it (an object\'s catalog page, its lineage graph, a run) so an assistant can hand the reader something to open. Empty emits root-relative links, which only resolve for a client rendering inside the GUI itself.')
param guiUrl string = ''

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('vCPU per replica; the server is a thin authenticated proxy, so the smallest consumption pair suffices.')
param cpu string = '0.25'

@description('Memory per replica, paired with cpu.')
param memory string = '0.5Gi'

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
        transport: 'http'
        allowInsecure: false
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
          name: 'sqlflow-mcp'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: [
            {
              name: 'SQLFLOW_CONTROL_PLANE_URL'
              value: controlPlaneUrl
            }
            {
              name: 'SQLFLOW_MCP_HTTP_BIND'
              value: '0.0.0.0:8080'
            }
            {
              name: 'SQLFLOW_GUI_URL'
              value: guiUrl
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 2
              periodSeconds: 3
              failureThreshold: 10
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 15
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // Exactly one replica: MCP sessions (Mcp-Session-Id) live in this process's memory, and the
        // streamable HTTP transport has no session affinity across replicas. One small replica
        // comfortably serves an internal assistant workload; scaling out would need a shared
        // session store, not more copies.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    acrPull
  ]
}

@description('The MCP endpoint URL to register with clients (Foundry MCP tool serverUrl).')
output mcpUrl string = 'https://${app.properties.configuration.ingress.fqdn}/mcp'

@description('The principal (object) id of the app identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId
