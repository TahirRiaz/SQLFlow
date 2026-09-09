// Deploys the SQLFlow Slack assistant as a Container App: SqlFlow.SlackBot, the Socket Mode worker that
// relays channel mentions and DMs to a model provider whose tools are the SQLFlow MCP server. Three
// provider modes share one experience; the bot dials OUT to Slack over a websocket and to the provider
// over HTTPS, so it exposes nothing (no ingress):
//
//   AzureFoundry  the Foundry Responses API, signed in with the app's managed identity (grant it the
//                 Cognitive Services OpenAI User role on the account; main.bicep does this via
//                 ai-foundry.bicep's agentPrincipalIds). No API key anywhere.
//   OpenAI        the OpenAI platform directly, authenticated with an OpenAI API key from Key Vault.
//                 No Azure AI dependency.
//   Anthropic     the Anthropic Claude API (MCP connector), authenticated with an Anthropic API key
//                 from Key Vault. No Azure AI dependency.
//
// Secrets (the two Slack tokens, the read-scoped SQLFlow access token, and for the OpenAI/Anthropic
// modes the provider API key) come from an existing Key Vault, read by the app's user-assigned
// managed identity.
//
//   az deployment group create -g <rg> -f slack-bot.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-slack-bot:latest keyVaultName=<kv> \
//        mcpServerUrl=https://.../mcp foundryProjectEndpoint=https://... foundryModelDeploymentName=gpt-5.1

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity).')
param name string = 'sqlflow-slack-bot'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-slack-bot:latest')
param image string

@description('Name of an existing Key Vault holding the Slack and SQLFlow token secrets.')
param keyVaultName string

@description('The model provider answering questions: AzureFoundry (managed identity, no key), OpenAI (platform API key), or Anthropic (Claude API key).')
@allowed(['AzureFoundry', 'OpenAI', 'Anthropic'])
param assistantProvider string = 'AzureFoundry'

@description('Key Vault secret name for the Slack app-level token (xapp-..., Socket Mode).')
param slackAppTokenSecretName string = 'sqlflow-slack-app-token'

@description('Key Vault secret name for the Slack bot user OAuth token (xoxb-...).')
param slackBotTokenSecretName string = 'sqlflow-slack-bot-token'

@description('Key Vault secret name for the read-scoped SQLFlow personal access token the assistant presents to the MCP server.')
param sqlflowAccessTokenSecretName string = 'sqlflow-slack-bot-access-token'

@description('Key Vault secret name for the provider API key. Read only when assistantProvider is OpenAI or Anthropic.')
param modelApiKeySecretName string = 'sqlflow-slack-bot-model-api-key'

@description('The Foundry project endpoint, e.g. https://<account>.services.ai.azure.com/api/projects/<project>. Required when assistantProvider is AzureFoundry.')
param foundryProjectEndpoint string = ''

@description('The model deployment (in the same Foundry account) the assistant runs on. Required when assistantProvider is AzureFoundry.')
param foundryModelDeploymentName string = ''

@description('The OpenAI model, which must support the Responses API with the hosted MCP tool. Used when assistantProvider is OpenAI.')
param openaiModel string = 'gpt-5-mini'

@description('The Claude model id. Used when assistantProvider is Anthropic.')
param anthropicModel string = 'claude-opus-4-8'

@description('The deployed SQLFlow MCP server endpoint the assistant uses as its tool source, e.g. https://sqlflow-mcp.<env-domain>/mcp.')
param mcpServerUrl string

@description('SQLFlow GUI base URL; when set, answers link runs and pipelines to their GUI pages. Empty disables links.')
param guiBaseUrl string = ''

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('vCPU per replica; a relay with no local model work needs the smallest consumption pair.')
param cpu string = '0.25'

@description('Memory per replica, paired with cpu.')
param memory string = '0.5Gi'

// The Key Vault Secrets User built-in role, so the app's identity can read the configured secrets.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
// The AcrPull built-in role, for managed-identity image pull from a same-group registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

var usesApiKey = assistantProvider != 'AzureFoundry'

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

var vaultUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'
var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

var baseSecrets = [
  {
    name: 'slack-app-token'
    keyVaultUrl: '${vaultUri}secrets/${slackAppTokenSecretName}'
    identity: identity.id
  }
  {
    name: 'slack-bot-token'
    keyVaultUrl: '${vaultUri}secrets/${slackBotTokenSecretName}'
    identity: identity.id
  }
  {
    name: 'sqlflow-access-token'
    keyVaultUrl: '${vaultUri}secrets/${sqlflowAccessTokenSecretName}'
    identity: identity.id
  }
]

var apiKeySecrets = usesApiKey ? [
  {
    name: 'model-api-key'
    keyVaultUrl: '${vaultUri}secrets/${modelApiKeySecretName}'
    identity: identity.id
  }
] : []

var baseEnv = [
  {
    name: 'SlackBot__Provider'
    value: assistantProvider
  }
  {
    name: 'SlackBot__Slack__AppToken'
    secretRef: 'slack-app-token'
  }
  {
    name: 'SlackBot__Slack__BotToken'
    secretRef: 'slack-bot-token'
  }
  {
    name: 'SlackBot__SqlFlow__AccessToken'
    secretRef: 'sqlflow-access-token'
  }
  {
    name: 'SlackBot__SqlFlow__GuiBaseUrl'
    value: guiBaseUrl
  }
  {
    name: 'SlackBot__Mcp__ServerUrl'
    value: mcpServerUrl
  }
]

var foundryEnv = assistantProvider == 'AzureFoundry' ? [
  {
    name: 'SlackBot__Foundry__ProjectEndpoint'
    value: foundryProjectEndpoint
  }
  {
    name: 'SlackBot__Foundry__ModelDeploymentName'
    value: foundryModelDeploymentName
  }
  // DefaultAzureCredential resolves this user-assigned identity for the Foundry sign-in.
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
] : []

var openaiEnv = assistantProvider == 'OpenAI' ? [
  {
    name: 'SlackBot__OpenAI__ApiKey'
    secretRef: 'model-api-key'
  }
  {
    name: 'SlackBot__OpenAI__Model'
    value: openaiModel
  }
] : []

var anthropicEnv = assistantProvider == 'Anthropic' ? [
  {
    name: 'SlackBot__Anthropic__ApiKey'
    secretRef: 'model-api-key'
  }
  {
    name: 'SlackBot__Anthropic__Model'
    value: anthropicModel
  }
] : []

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
      // No ingress: Socket Mode dials out, the model provider and Key Vault are outbound HTTPS.
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: concat(baseSecrets, apiKeySecrets)
    }
    template: {
      containers: [
        {
          name: 'slack-bot'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(baseEnv, foundryEnv, openaiEnv, anthropicEnv)
        }
      ]
      scale: {
        // Exactly one replica: the thread map and event dedupe are in-process, and one connection
        // comfortably serves a workspace. Slack would load-balance events across replicas, so a
        // second copy would split conversation context, not add safety.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    keyVaultAccess
    acrPull
  ]
}

@description('The principal (object) id of the bot identity: in AzureFoundry mode, grant it Foundry access (ai-foundry.bicep agentPrincipalIds).')
output identityPrincipalId string = identity.properties.principalId

@description('The client id of the bot identity.')
output identityClientId string = identity.properties.clientId
