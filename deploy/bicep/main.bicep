// The full SQLFlow estate on Azure Container Apps in one resource-group deployment: Log Analytics and the
// Container Apps environment, a Key Vault holding every secret, the three Azure SQL databases every estate
// has (catalog, pre, dwh), and the three apps composed from the per-tier templates in this directory:
//
//   gui.bicep            the SPA, external ingress; calls the control plane cross-origin
//   control-plane.bicep  the API (in-process worker OFF: API replicas do API work only), CORS'd to the GUI
//   worker.bicep         one worker pool, no ingress, scaled 0..N on catalog queue depth
//
// Secrets flow one way: the deployment writes them into Key Vault, each app's user-assigned managed identity
// reads them at start, and the same identities resolve ${keyvault:...} references at run time. No secret value
// appears in app configuration. The deploying principal needs the Key Vault Secrets Officer (or Key Vault
// Administrator) data-plane role to write the secrets, since the vault uses RBAC authorization.
//
// Build and push the three images first (see deploy/README.md), then:
//
//   az deployment group create -g <rg> -f main.bicep \
//     -p acrName=<registry> \
//        controlPlaneImage=<registry>.azurecr.io/sqlflow-control-plane:latest \
//        workerImage=<registry>.azurecr.io/sqlflow-worker:latest \
//        guiImage=<registry>.azurecr.io/sqlflow-gui:latest \
//        sqlAdminPassword=<...> jwtSigningKey=<...> adminPassword=<...>
//
// On first start, bootstrap provisioning applies the catalog migrations and creates the admin user, so the
// GUI is sign-in ready at the guiUrl output. Add worker pools by deploying worker.bicep again with a distinct
// name and pool.

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Container image reference for the control plane, e.g. myregistry.azurecr.io/sqlflow-control-plane:latest')
param controlPlaneImage string

@description('Container image reference for the worker, e.g. myregistry.azurecr.io/sqlflow-worker:latest')
param workerImage string

@description('Container image reference for the GUI, e.g. myregistry.azurecr.io/sqlflow-gui:latest')
param guiImage string

@description('Name of a container registry in THIS resource group holding the three images: each app identity is granted AcrPull on it. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the three app identities yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Address of an existing SQL Server to host the catalog instead of creating one: host or host,port (a Managed Instance private FQDN, a public-endpoint address with ,3342, or any reachable SQL Server). Empty creates an Azure SQL logical server + database here. With an existing server the network path is yours to provide (for a VNet-only Managed Instance, set infrastructureSubnetId so the apps egress inside its VNet) and the catalog database is created by bootstrap on first start.')
param existingSqlServer string = ''

@description('Resource id of a vNet subnet to integrate the Container Apps environment into (consumption architecture: an undelegated subnet of at least /23). Empty deploys the environment without VNet integration.')
param infrastructureSubnetId string = ''

@description('SQL admin login: for the server created here, or the existing server\'s login when existingSqlServer is set.')
param sqlAdminLogin string = 'sqlflowadmin'

@secure()
@description('SQL admin password (Azure SQL enforces complexity). Single quotes and semicolons are handled; the value is quoted into the connection string.')
param sqlAdminPassword string

@secure()
@minLength(32)
@description('HS256 signing key for control plane tokens: at least 32 bytes of randomness, e.g. openssl rand -base64 48.')
param jwtSigningKey string

@description('Username of the initial admin user bootstrap provisioning creates on first start.')
param adminUsername string = 'admin'

@secure()
@minLength(12)
@description('Password for the initial admin user (at least 12 characters).')
param adminPassword string

@secure()
@description('Token for private git remotes: managed sync on the control plane fetches with it, workers materialize with it. Leave empty when every registered repo is public.')
param gitToken string = ''

@description('A personal access token minted with the node scope, which the worker pool presents to the control plane\'s dispatcher. The control plane must be running to mint one (an admin: POST /api/v1/me/tokens with scopes ["node"]), so a first deployment leaves this empty and re-runs with it once the control plane is up; until then the worker deploys without a credential and takes no work.')
@secure()
param nodeToken string = ''

@description('Username paired with gitToken when the host requires one: a Bitbucket app password takes the account username, a Bitbucket repository access token takes x-token-auth; GitHub ignores it. Empty sends the token alone.')
param gitUsername string = ''

@description('Let this template create and own the Entra app registration users sign in with (App Roles + "Assignment required" baked in, so only assigned group members can sign in), through the Microsoft Graph Bicep extension. On (the default) enables "Sign in with Microsoft" and ignores azureAdAllowedTenantIds/azureAdClientId; the deploying principal then needs directory write (Application Administrator or Application.ReadWrite.All), and assigning a group needs Entra ID P1. Turn off for local-only sign-in, or to point at an externally managed registration via azureAdAllowedTenantIds + azureAdClientId.')
param provisionEntraApp bool = true

@description('Object id of the Entra security group whose members may sign in when provisionEntraApp is on. Only its members are assigned the app role and can obtain a token. Empty still enforces assignment required, so no one signs in until a group or users are assigned in the enterprise application. Reaches only this app\'s home tenant (subscription().tenantId); a tenant named in azureAdAdditionalAllowedTenantIds assigns its own users itself. Ignored when provisionEntraApp is off.')
param azureAdAllowedGroupObjectId string = ''

@description('Display name for the app registration this template creates when provisionEntraApp is on.')
param entraAppDisplayName string = 'SQLFlow'

@description('Stable unique name (Graph identity key) for that registration, so redeploys update the same app. Lowercase, no spaces.')
param entraAppUniqueName string = 'sqlflow'

@description('PROVISIONED app mode only (provisionEntraApp on): further Entra tenant (directory) ids, beyond this deployment\'s own home tenant (always trusted), whose users may also sign in. A non-empty list makes the app registration multi-tenant automatically; each named tenant\'s own admin must still consent to the app once and then assign the SqlFlow.User role to their own users/groups (this template has no directory access into a tenant it does not own); see the entraForeignTenantReminder output for that step. Empty keeps the app single-tenant.')
param azureAdAdditionalAllowedTenantIds array = []

@description('EXTERNAL app mode only (provisionEntraApp off): Microsoft Entra tenant (directory) ids allowed for GUI single sign-on, against an app registration you manage yourself. Set together with azureAdClientId to offer "Sign in with Microsoft"; leave empty for local sign-in only. Register the GUI origin (the guiUrl output) as a redirect URI on that SPA registration, and make it multi-tenant yourself if this list has more than one entry.')
param azureAdAllowedTenantIds array = []

@description('EXTERNAL app mode only (provisionEntraApp off): client id of the SPA app registration users sign in with. Set together with azureAdAllowedTenantIds to enable SSO. Ignored when provisionEntraApp is on (this template creates the registration and supplies the client id itself).')
param azureAdClientId string = ''

@description('Role a first-time SSO user is provisioned with (least privilege by default; an admin raises it afterwards in the GUI).')
param azureAdDefaultRole string = 'viewer'

@description('ADDITIONAL flow environment references beyond the built-in SQLFLOW_CONN_PRE and SQLFLOW_CONN_DWH, one object per \${env:...} reference the worker pool\'s flows use: { name: the environment variable, secretName: an EXISTING Key Vault secret in keyVaultName holding its value }, e.g. [{ name: \'SQLFLOW_CONN_ERP\', secretName: \'erp-source-conn\' }]. Data-source credentials are put in the vault out of band and never pass through this template; the worker reads them under its own identity, so they never reach the control plane.')
param workerFlowEnv array = []

@description('Name for an Azure AI Foundry account (also its endpoint subdomain, globally unique) deployed alongside the estate, with the control plane and worker identities granted caller access. Empty skips AI Foundry.')
param aiFoundryName string = ''

@description('Name of the Foundry project created under the AI Foundry account.')
param aiFoundryProjectName string = 'sqlflow'

@description('Model deployed under the AI Foundry account, e.g. gpt-5.1 (pick one the region still accepts for new deployments: az cognitiveservices model list). Required by the Slack assistant\'s agent; empty deploys no model.')
param aiFoundryModelName string = ''

@description('Version of that model. Empty lets the service pick the current default version.')
param aiFoundryModelVersion string = ''

@description('Capacity for the model deployment, in thousands of tokens per minute.')
param aiFoundryModelCapacity int = 30

@description('Optional audio-transcription model deployed beside the chat model (e.g. gpt-4o-mini-transcribe), enabling the GUI chat assistant\'s server-side voice input. Empty deploys none; the mic then falls back to the browser\'s built-in speech recognition.')
param aiFoundryTranscriptionModelName string = ''

@description('Version of the transcription model. Empty lets the service pick the current default version.')
param aiFoundryTranscriptionModelVersion string = ''

@description('Container image for the SQLFlow MCP server in HTTP mode, e.g. <registry>/sqlflow-mcp:latest (built from Dockerfile.mcp). Empty skips it, and with it the Slack assistant.')
param mcpImage string = ''

@description('Container image for the Slack assistant, e.g. <registry>/sqlflow-slack-bot:latest (built from Dockerfile.slackbot). Empty skips the Slack assistant.')
param slackBotImage string = ''

@secure()
@description('Slack app-level token (xapp-...) with connections:write, from the Slack app created with deploy/slack/manifest.yaml. Required for the Slack assistant.')
param slackAppToken string = ''

@secure()
@description('Slack bot user OAuth token (xoxb-...) of that app. Required for the Slack assistant.')
param slackBotToken string = ''

@secure()
@description('A READ-scoped SQLFlow personal access token (sqlf_...) the assistant presents to the MCP server. Mint it in the GUI or CLI after the estate is up, then redeploy with this set; empty skips the Slack assistant.')
param slackBotSqlflowToken string = ''

@description('The Slack assistant model provider: AzureFoundry (the Foundry Responses API via managed identity, needs aiFoundryName + aiFoundryModelName), OpenAI (the OpenAI platform via API key), or Anthropic (the Claude API via API key). The two key-based modes have no Azure AI dependency.')
@allowed(['AzureFoundry', 'OpenAI', 'Anthropic'])
param slackBotProvider string = 'AzureFoundry'

@secure()
@description('The provider API key for the OpenAI (sk-...) or Anthropic (sk-ant-...) mode. Required for those modes and ignored for AzureFoundry; empty in a key-based mode skips the Slack assistant.')
param slackBotModelApiKey string = ''

@description('The OpenAI model, which must support the Responses API with the hosted MCP tool. Used when slackBotProvider is OpenAI.')
param slackBotOpenAIModel string = 'gpt-5-mini'

@description('The Claude model id. Used when slackBotProvider is Anthropic.')
param slackBotAnthropicModel string = 'claude-opus-4-8'

@description('Name of the Container App running the MCP server.')
param mcpName string = 'sqlflow-mcp'

@description('Name of the Container App running the Slack assistant.')
param slackBotName string = 'sqlflow-slack-bot'

@description('The pool the worker app serves. Empty drains untargeted runs only.')
param workerPool string = ''

@description('Upper bound for worker queue-depth scale out (one queued run per replica).')
@minValue(1)
param workerMaxReplicas int = 10

@description('Minimum control plane replicas. Keep at 1 so the API is warm (a trigger never waits on a cold start).')
@minValue(1)
param controlPlaneMinReplicas int = 1

@description('Maximum control plane replicas. Keep at 1: the run queue is owned by exactly one replica (the dispatch lease), so an extra replica adds no dispatch capacity and refuses every node call that lands on it (503, retried by the node), which only slows hand-outs. Raise it only once passive replicas forward node calls to the owner.')
@minValue(1)
param controlPlaneMaxReplicas int = 1

@description('Name of the Container App running the control plane.')
param controlPlaneName string = 'sqlflow-control-plane'

@description('Name of the Container App running the worker pool.')
param workerName string = 'sqlflow-worker'

@description('Name of the Container App running the GUI.')
param guiName string = 'sqlflow-gui'

@description('Name of the Container Apps managed environment.')
param environmentName string = 'sqlflow-env'

@description('Name of the Log Analytics workspace receiving container logs.')
param logAnalyticsName string = 'sqlflow-logs'

@minLength(3)
@maxLength(24)
@description('Key Vault name (globally unique, 3-24 alphanumerics and hyphens).')
param keyVaultName string = 'sqlflow-kv-${uniqueString(resourceGroup().id)}'

@description('Azure SQL logical server name (globally unique, lowercase).')
param sqlServerName string = 'sqlflow-sql-${uniqueString(resourceGroup().id)}'

@description('Name of the catalog database on that server.')
param catalogDatabaseName string = 'SqlFlowCatalog'

@description('Name of the staging database flows land raw ingests in, reachable from flow YAML as \${env:SQLFLOW_CONN_PRE}.')
param preDatabaseName string = 'SqlFlowPre'

@description('Name of the warehouse database flows publish modelled data to, reachable from flow YAML as \${env:SQLFLOW_CONN_DWH}.')
param dwhDatabaseName string = 'SqlFlowDwh'

@description('Catalog database SKU (ignored when existingSqlServer is set). The catalog is metadata plus the run queue: modest, but polled continuously, so avoid serverless auto-pause.')
param sqlDatabaseSku object = {
  name: 'S1'
  tier: 'Standard'
}

@description('SKU for the pre and dwh databases (ignored when existingSqlServer is set). These carry the data, so they are sized apart from the catalog.')
param dataDatabaseSku object = {
  name: 'S1'
  tier: 'Standard'
}

// Secret names shared with the per-tier templates (their defaults match these).
var catalogConnectionSecretName = 'sqlflow-catalog-db'
var preConnectionSecretName = 'sqlflow-pre-db'
var dwhConnectionSecretName = 'sqlflow-dwh-db'
var jwtSigningKeySecretName = 'sqlflow-jwt-signing-key'
var adminPasswordSecretName = 'sqlflow-admin-password'
var gitTokenSecretName = 'sqlflow-git-token'
var nodeTokenSecretName = 'sqlflow-node-token'
var slackAppTokenSecretName = 'sqlflow-slack-app-token'
var slackBotTokenSecretName = 'sqlflow-slack-bot-token'
var slackBotSqlflowTokenSecretName = 'sqlflow-slack-bot-access-token'
var slackBotModelApiKeySecretName = 'sqlflow-slack-bot-model-api-key'

// The MCP server deploys on its own (any MCP client can use it); the Slack assistant additionally needs the
// Slack app, a SQLFlow access token, and its model provider: a Foundry account with a model in AzureFoundry
// mode, or the provider API key in the OpenAI/Anthropic modes. Anything missing simply leaves the assistant
// out of this deployment; the rest of the estate is unaffected.
var mcpEnabled = !empty(mcpImage)
// The GUI chat assistant rides on the same building blocks (a Foundry model + the MCP server) and
// needs nothing else, so it lights up automatically once both exist. It shares the assistant core
// with the Slack bot but not its identity: every chat run carries the signed-in user's own bearer.
var chatAssistantEnabled = mcpEnabled && !empty(aiFoundryName) && !empty(aiFoundryModelName)
var slackBotUsesApiKey = slackBotProvider != 'AzureFoundry'
var slackBotProviderReady = slackBotUsesApiKey
  ? !empty(slackBotModelApiKey)
  : (!empty(aiFoundryName) && !empty(aiFoundryModelName))
var slackBotEnabled = mcpEnabled && !empty(slackBotImage) && !empty(slackAppToken) && !empty(slackBotToken) && !empty(slackBotSqlflowToken) && slackBotProviderReady

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: empty(infrastructureSubnetId) ? null : {
      infrastructureSubnetId: infrastructureSubnetId
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (empty(existingSqlServer)) {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// The 0.0.0.0 rule is Azure's "allow Azure services" switch: the Container Apps consumption plan has no fixed
// outbound address for a narrower rule. To close the catalog to the public internet, run the environment in a
// VNet and reach SQL over a private endpoint instead (then disable public access and drop this rule).
resource sqlAllowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource catalogDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: catalogDatabaseName
  location: location
  sku: sqlDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// The two data databases every estate has: pre stages raw ingests, dwh holds the modelled result. Like the
// catalog they are only created when this template creates the server; on an existing server (a Managed
// Instance) the databases are provisioned out of band, and only their connection secrets are wired here.
resource preDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: preDatabaseName
  location: location
  sku: dataDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

resource dwhDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: dwhDatabaseName
  location: location
  sku: dataDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// host,port for the connection string: the created server on the standard port, or the existing address with
// its own port when it carries one (a Managed Instance public endpoint is host,3342).
var catalogServerAddress = empty(existingSqlServer)
  ? '${sqlServer!.properties.fullyQualifiedDomainName},1433'
  : (contains(existingSqlServer, ',') ? existingSqlServer : '${existingSqlServer},1433')

// The one place the catalog connection string exists; everything else references the vault secret. The SQL
// admin is used because logins cannot be created from ARM; switching the apps to least-privilege credentials
// (or Entra-authenticated access for their managed identities) later means updating only this secret. The
// password is quoted (embedded single quotes doubled) so any complex value survives ADO.NET parsing.
var quotedSqlAdminPassword = '\'${replace(sqlAdminPassword, '\'', '\'\'')}\''
var catalogConnectionString = 'Server=tcp:${catalogServerAddress};Initial Catalog=${catalogDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'

// The pre and dwh connection strings, same server and credential as the catalog, differing only in the
// database. Flows reach them by the fixed names ${env:SQLFLOW_CONN_PRE} and ${env:SQLFLOW_CONN_DWH}, so a
// document moves between estates unchanged: only these secrets' values differ.
var preConnectionString = 'Server=tcp:${catalogServerAddress};Initial Catalog=${preDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'
var dwhConnectionString = 'Server=tcp:${catalogServerAddress};Initial Catalog=${dwhDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'

resource catalogDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: catalogConnectionSecretName
  properties: {
    value: catalogConnectionString
  }
}

resource preDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: preConnectionSecretName
  properties: {
    value: preConnectionString
  }
}

resource dwhDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: dwhConnectionSecretName
  properties: {
    value: dwhConnectionString
  }
}

resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: jwtSigningKeySecretName
  properties: {
    value: jwtSigningKey
  }
}

resource adminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: adminPasswordSecretName
  properties: {
    value: adminPassword
  }
}

resource gitTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(gitToken)) {
  parent: keyVault
  name: gitTokenSecretName
  properties: {
    value: gitToken
  }
}

resource nodeTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(nodeToken)) {
  parent: keyVault
  name: nodeTokenSecretName
  properties: {
    value: nodeToken
  }
}

resource slackAppTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (slackBotEnabled) {
  parent: keyVault
  name: slackAppTokenSecretName
  properties: {
    value: slackAppToken
  }
}

resource slackBotTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (slackBotEnabled) {
  parent: keyVault
  name: slackBotTokenSecretName
  properties: {
    value: slackBotToken
  }
}

resource slackBotSqlflowTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (slackBotEnabled) {
  parent: keyVault
  name: slackBotSqlflowTokenSecretName
  properties: {
    value: slackBotSqlflowToken
  }
}

resource slackBotModelApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (slackBotEnabled && slackBotUsesApiKey) {
  parent: keyVault
  name: slackBotModelApiKeySecretName
  properties: {
    value: slackBotModelApiKey
  }
}

// Each app's FQDN is <app>.<environment default domain>, so the cross-origin wiring (GUI -> API base URL,
// control plane -> CORS origin) is computed up front instead of creating a dependency cycle between the apps.
var controlPlaneFqdn = '${controlPlaneName}.${managedEnvironment.properties.defaultDomain}'
var guiFqdn = '${guiName}.${managedEnvironment.properties.defaultDomain}'

// A non-empty azureAdAdditionalAllowedTenantIds means a second organization needs to sign in too, so the
// registration itself must be multi-tenant; otherwise it stays single-tenant (the secure-by-default case).
var entraSignInAudience = empty(azureAdAdditionalAllowedTenantIds) ? 'AzureADMyOrg' : 'AzureADMultipleOrgs'

// The Entra app users sign in with: created and owned here (App Roles + assignment required) when
// provisionEntraApp is on, otherwise an externally managed registration referenced by the azureAd* params.
// The GUI origin is the SPA redirect URI, known up front from the environment's default domain.
module entraApp 'entra-app.bicep' = if (provisionEntraApp) {
  name: 'sqlflow-entra-app'
  params: {
    displayName: entraAppDisplayName
    uniqueName: entraAppUniqueName
    redirectUri: 'https://${guiFqdn}'
    allowedGroupObjectId: azureAdAllowedGroupObjectId
    signInAudience: entraSignInAudience
  }
}

// This deployment's own subscription tenant is always trusted when the app is provisioned here; in external
// mode the full allow-list comes from the caller. Either way this is what the control plane validates tokens
// against, and an empty client id leaves SSO off (local sign-in only).
var effectiveAzureAdAllowedTenantIds = provisionEntraApp
  ? concat([subscription().tenantId], azureAdAdditionalAllowedTenantIds)
  : azureAdAllowedTenantIds
var effectiveAzureAdClientId = provisionEntraApp ? entraApp!.outputs.clientId : azureAdClientId

module controlPlane 'control-plane.bicep' = {
  name: 'sqlflow-control-plane-app'
  params: {
    location: location
    name: controlPlaneName
    managedEnvironmentId: managedEnvironment.id
    image: controlPlaneImage
    keyVaultName: keyVault.name
    catalogConnectionSecretName: catalogConnectionSecretName
    jwtSigningKeySecretName: jwtSigningKeySecretName
    // Compute belongs to the worker app; API replicas do API + scheduler + sync work only.
    workerEnabled: false
    // The GUI is a separate origin on Container Apps (one ingress FQDN per app).
    corsAllowedOrigins: [
      'https://${guiFqdn}'
    ]
    bootstrapAdminUsername: adminUsername
    bootstrapAdminPasswordSecretName: adminPasswordSecretName
    gitTokenSecretName: empty(gitToken) ? '' : gitTokenSecretName
    gitUsername: gitUsername
    azureAdAllowedTenantIds: effectiveAzureAdAllowedTenantIds
    azureAdClientId: effectiveAzureAdClientId
    azureAdDefaultRole: azureAdDefaultRole
    acrName: acrName
    acrLoginServer: acrLoginServer
    // The GUI chat assistant, on the estate's own Foundry account and MCP server (see chatAssistantEnabled).
    assistantEnabled: chatAssistantEnabled
    assistantFoundryProjectEndpoint: chatAssistantEnabled ? aiFoundryProjectEndpoint : ''
    assistantFoundryModelDeploymentName: chatAssistantEnabled ? aiFoundryModelName : ''
    assistantFoundryTranscriptionDeploymentName: chatAssistantEnabled ? aiFoundryTranscriptionModelName : ''
    assistantMcpServerUrl: chatAssistantEnabled ? mcp!.outputs.mcpUrl : ''
    minReplicas: controlPlaneMinReplicas
    maxReplicas: controlPlaneMaxReplicas
  }
  // The app reads these vault secrets at creation and migrates the catalog database at startup.
  dependsOn: [
    catalogDbSecret
    jwtSigningKeySecret
    adminPasswordSecret
    gitTokenSecret
    catalogDatabase
    sqlAllowAzureServices
  ]
}

// The two data databases are wired under fixed names in every estate, so a flow document referencing
// ${env:SQLFLOW_CONN_PRE} or ${env:SQLFLOW_CONN_DWH} moves from test to prod unchanged. Caller-supplied
// data-source references follow, and must not reuse these two names.
var builtInFlowEnv = [
  {
    name: 'SQLFLOW_CONN_PRE'
    secretName: preConnectionSecretName
  }
  {
    name: 'SQLFLOW_CONN_DWH'
    secretName: dwhConnectionSecretName
  }
]

module worker 'worker.bicep' = {
  name: 'sqlflow-worker-app'
  params: {
    location: location
    name: workerName
    managedEnvironmentId: managedEnvironment.id
    image: workerImage
    keyVaultName: keyVault.name
    pool: workerPool
    controlPlaneUrl: 'https://${controlPlaneFqdn}'
    nodeTokenSecretName: empty(nodeToken) ? '' : nodeTokenSecretName
    gitTokenSecretName: empty(gitToken) ? '' : gitTokenSecretName
    gitUsername: gitUsername
    flowEnv: concat(builtInFlowEnv, workerFlowEnv)
    acrName: acrName
    acrLoginServer: acrLoginServer
    maxReplicas: workerMaxReplicas
  }
  dependsOn: [
    catalogDbSecret
    preDbSecret
    dwhDbSecret
    gitTokenSecret
    nodeTokenSecret
    catalogDatabase
    preDatabase
    dwhDatabase
    sqlAllowAzureServices
  ]
}

module gui 'gui.bicep' = {
  name: 'sqlflow-gui-app'
  params: {
    location: location
    name: guiName
    managedEnvironmentId: managedEnvironment.id
    image: guiImage
    apiBaseUrl: 'https://${controlPlaneFqdn}'
    acrName: acrName
    acrLoginServer: acrLoginServer
  }
}

// Optional: an AI Foundry account + project beside the estate. The app identities are granted caller access
// (Cognitive Services User), so anything they run can call models keylessly; the Slack assistant's identity
// is granted the Azure AI User role so it can drive the Foundry Agent Service.
module aiFoundry 'ai-foundry.bicep' = if (!empty(aiFoundryName)) {
  name: 'sqlflow-ai-foundry'
  params: {
    location: location
    name: aiFoundryName
    projectName: aiFoundryProjectName
    modelName: aiFoundryModelName
    modelVersion: aiFoundryModelVersion
    modelCapacity: aiFoundryModelCapacity
    transcriptionModelName: aiFoundryTranscriptionModelName
    transcriptionModelVersion: aiFoundryTranscriptionModelVersion
    userPrincipalIds: [
      controlPlane.outputs.identityPrincipalId
      worker.outputs.identityPrincipalId
    ]
    // The OpenAI-inference role (Responses API + audio transcription): the Slack bot's identity when
    // it runs on Foundry, and the control plane's when the GUI chat assistant is on. The key-based
    // providers never touch the account.
    agentPrincipalIds: concat(
      (slackBotEnabled && !slackBotUsesApiKey) ? [
        slackBot!.outputs.identityPrincipalId
      ] : [],
      chatAssistantEnabled ? [
        controlPlane.outputs.identityPrincipalId
      ] : [])
  }
}

// The MCP server over streamable HTTP: the tool source for the Foundry agent, and for any other remote MCP
// client that presents a SQLFlow bearer token.
module mcp 'mcp.bicep' = if (mcpEnabled) {
  name: 'sqlflow-mcp-app'
  params: {
    location: location
    name: mcpName
    managedEnvironmentId: managedEnvironment.id
    image: mcpImage
    controlPlaneUrl: 'https://${controlPlaneFqdn}'
    guiUrl: 'https://${guiFqdn}'
    acrName: acrName
    acrLoginServer: acrLoginServer
  }
}

// The Foundry project endpoint is deterministic from the account and project names, so the Slack bot module
// never has to wait on (or cycle with) the ai-foundry module, which in turn references the bot's identity
// for its agent role grant.
var aiFoundryProjectEndpoint = empty(aiFoundryName) ? '' : 'https://${aiFoundryName}.services.ai.azure.com/api/projects/${aiFoundryProjectName}'

module slackBot 'slack-bot.bicep' = if (slackBotEnabled) {
  name: 'sqlflow-slack-bot-app'
  params: {
    location: location
    name: slackBotName
    managedEnvironmentId: managedEnvironment.id
    image: slackBotImage
    keyVaultName: keyVault.name
    assistantProvider: slackBotProvider
    slackAppTokenSecretName: slackAppTokenSecretName
    slackBotTokenSecretName: slackBotTokenSecretName
    sqlflowAccessTokenSecretName: slackBotSqlflowTokenSecretName
    modelApiKeySecretName: slackBotModelApiKeySecretName
    foundryProjectEndpoint: aiFoundryProjectEndpoint
    foundryModelDeploymentName: aiFoundryModelName
    openaiModel: slackBotOpenAIModel
    anthropicModel: slackBotAnthropicModel
    mcpServerUrl: mcp!.outputs.mcpUrl
    guiBaseUrl: 'https://${guiFqdn}'
    acrName: acrName
    acrLoginServer: acrLoginServer
  }
  dependsOn: [
    slackAppTokenSecret
    slackBotTokenSecret
    slackBotSqlflowTokenSecret
    slackBotModelApiKeySecret
  ]
}

@description('Sign in here with the bootstrap admin (adminUsername/adminPassword).')
output guiUrl string = gui.outputs.guiUrl

@description('Client id of the Entra registration users sign in with: created and owned by this template when provisionEntraApp is on, otherwise the azureAdClientId you passed. Empty means SSO is off (local sign-in only).')
output entraClientId string = effectiveAzureAdClientId

@description('The GUI origin registered as the SPA redirect URI. When provisionEntraApp is on this template already set it on the registration; in external mode add it yourself (Single-page application platform). Empty when SSO is off.')
output entraRedirectUri string = empty(effectiveAzureAdClientId) ? '' : gui.outputs.guiUrl

@description('Next step for Bicep-owned SSO: assignment is required, so no one can sign in until members are assigned. Set azureAdAllowedGroupObjectId to a security group to grant its members access, or assign users/groups in the enterprise application. Empty when a group was already assigned or SSO is off.')
output entraAssignmentReminder string = (provisionEntraApp && empty(azureAdAllowedGroupObjectId)) ? 'Assign a group via azureAdAllowedGroupObjectId (or in the enterprise application) so members can sign in; assignment is required and none are assigned yet.' : ''

@description('Next step for each tenant named in azureAdAdditionalAllowedTenantIds: this template cannot grant role assignments outside its own home tenant, so an admin in that tenant must consent to the app (creates their own local enterprise application object) and then assign the SqlFlow.User role to their own users or groups on it. Empty when no additional tenant was configured or SSO is off.')
output entraForeignTenantReminder string = (provisionEntraApp && !empty(azureAdAdditionalAllowedTenantIds))
  ? 'For each tenant in azureAdAdditionalAllowedTenantIds: an admin there must consent to app ${effectiveAzureAdClientId}, then assign the SqlFlow.User role to their own users/groups in their own enterprise application view.'
  : ''

@description('The API base URL: point the ADF pipeline (deploy/adf) and CLI remotes at this.')
output controlPlaneBaseUrl string = controlPlane.outputs.controlPlaneBaseUrl

@description('Client id of the control plane identity, for granting access to flow secrets beyond this vault.')
output controlPlaneIdentityClientId string = controlPlane.outputs.identityClientId

@description('Client id of the worker identity, for granting access to the data its pool\'s flows touch.')
output workerIdentityClientId string = worker.outputs.identityClientId

@description('Catalog SQL server address (the catalog connection string is in Key Vault as sqlflow-catalog-db).')
output sqlServerFqdn string = empty(existingSqlServer) ? sqlServer!.properties.fullyQualifiedDomainName : existingSqlServer

@description('The vault holding every estate secret; put flow credentials here as \${keyvault:...} targets.')
output keyVaultUri string = keyVault.properties.vaultUri

@description('The AI Foundry endpoint, or empty when aiFoundryName was not set.')
output aiFoundryEndpoint string = empty(aiFoundryName) ? '' : aiFoundry!.outputs.endpoint

@description('The Foundry project endpoint agent clients connect to, or empty when aiFoundryName was not set.')
output aiFoundryProjectEndpoint string = aiFoundryProjectEndpoint

@description('The MCP endpoint URL (register in Foundry, VS Code, or any remote MCP client), or empty when mcpImage was not set.')
output mcpUrl string = mcpEnabled ? mcp!.outputs.mcpUrl : ''
