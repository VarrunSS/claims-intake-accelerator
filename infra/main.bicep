// Claims Intake Accelerator: Milestone 1 infrastructure skeleton.
//
// NOT YET DEPLOYED OR VALIDATED. The az and azd CLIs were not available when this was written, so the
// template has not been through `az bicep build`, what-if or a deployment. See README.md.
//
// Deploy with `azd up`. Everything lives in UK South (data residency, docs/00-discovery.md).

targetScope = 'subscription'

@minLength(1)
@maxLength(32)
@description('Name of the azd environment. Used in resource names and tags.')
param environmentName string

@allowed([
  'uksouth'
])
@description('UK South only. Claim documents hold personal data that must stay in the UK.')
param location string = 'uksouth'

@description('Turn off only in throwaway environments. Purge protection cannot be removed once enabled.')
param enableKeyVaultPurgeProtection bool = true

@allowed([
  'free'
  'basic'
])
@description('Azure AI Search tier. free costs nothing but there is one per subscription and it has no SLA; basic is about 70 pounds a month.')
param searchSku string = 'free'

var tags = {
  'azd-env-name': environmentName
  workload: 'claims-intake-accelerator'
  customer: 'thames-mutual-fictional'
}

// A short, stable suffix so globally unique names do not clash between environments.
var token = toLower(uniqueString(subscription().id, environmentName, location))

var names = {
  resourceGroup: 'rg-${environmentName}'
  identity: 'id-${environmentName}-${take(token, 6)}'
  logAnalytics: 'log-${environmentName}-${take(token, 6)}'
  applicationInsights: 'appi-${environmentName}-${take(token, 6)}'
  inboxStorage: 'stinbox${take(token, 13)}'
  hostStorage: 'sthost${take(token, 13)}'
  search: 'srch-${environmentName}-${take(token, 6)}'
  serviceBus: 'sb-${environmentName}-${take(token, 6)}'
  keyVault: 'kv-${take(environmentName, 8)}-${take(token, 8)}'
  functionPlan: 'plan-func-${environmentName}-${take(token, 6)}'
  functionApp: 'func-${environmentName}-${take(token, 6)}'
  webPlan: 'plan-web-${environmentName}-${take(token, 6)}'
  api: 'app-api-${environmentName}-${take(token, 6)}'
  legacy: 'app-legacy-${environmentName}-${take(token, 6)}'
}

var documentsQueueName = 'claim-documents'
// The legacy mock's address is known from its name, so the Function App and the API can be told it up front.
var legacyBaseUrl = 'https://${names.legacy}.azurewebsites.net'
// The same for the API: the pipeline reads it for the duplicate check.
var apiBaseUrl = 'https://${names.api}.azurewebsites.net'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: names.resourceGroup
  location: location
  tags: tags
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: {
    name: names.identity
    location: location
    tags: tags
  }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    logAnalyticsName: names.logAnalytics
    applicationInsightsName: names.applicationInsights
    location: location
    tags: tags
  }
}

module inboxStorage 'modules/storage.bicep' = {
  name: 'inbox-storage'
  scope: rg
  params: {
    name: names.inboxStorage
    location: location
    tags: tags
    containerNames: [
      'inbox'
    ]
  }
}

module hostStorage 'modules/storage.bicep' = {
  name: 'host-storage'
  scope: rg
  params: {
    name: names.hostStorage
    location: location
    tags: tags
    containerNames: [
      'app-package'
    ]
    // Audit log of agent tool calls. The identity already has Storage Table Data Contributor on this account.
    tableNames: [
      'auditlog'
    ]
  }
}

module search 'modules/search.bicep' = {
  name: 'search'
  scope: rg
  params: {
    name: names.search
    location: location
    tags: tags
    sku: searchSku
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'service-bus'
  scope: rg
  params: {
    name: names.serviceBus
    location: location
    tags: tags
    documentsQueueName: documentsQueueName
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'key-vault'
  scope: rg
  params: {
    name: names.keyVault
    location: location
    tags: tags
    enablePurgeProtection: enableKeyVaultPurgeProtection
  }
}

module functionApp 'modules/functionapp.bicep' = {
  name: 'function-app'
  scope: rg
  params: {
    planName: names.functionPlan
    functionAppName: names.functionApp
    location: location
    tags: tags
    identityId: identity.outputs.id
    identityClientId: identity.outputs.clientId
    hostStorageName: hostStorage.outputs.name
    inboxBlobEndpoint: inboxStorage.outputs.blobEndpoint
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
    documentsQueueName: documentsQueueName
    legacyBaseUrl: legacyBaseUrl
    apiBaseUrl: apiBaseUrl
    searchEndpoint: search.outputs.endpoint
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
  }
}

module appService 'modules/appservice.bicep' = {
  name: 'app-service'
  scope: rg
  params: {
    planName: names.webPlan
    apiName: names.api
    legacyName: names.legacy
    location: location
    tags: tags
    identityId: identity.outputs.id
    identityClientId: identity.outputs.clientId
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
    keyVaultUri: keyVault.outputs.uri
    legacyBaseUrl: legacyBaseUrl
    auditTableEndpoint: hostStorage.outputs.tableEndpoint
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
  }
}

module roles 'modules/roles.bicep' = {
  name: 'roles'
  scope: rg
  params: {
    principalId: identity.outputs.principalId
    hostStorageName: hostStorage.outputs.name
    inboxStorageName: inboxStorage.outputs.name
    serviceBusName: serviceBus.outputs.name
    keyVaultName: keyVault.outputs.name
    applicationInsightsName: monitoring.outputs.applicationInsightsName
    searchServiceName: search.outputs.name
  }
}

// azd reads these outputs into the environment.
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_USER_ASSIGNED_IDENTITY_CLIENT_ID string = identity.outputs.clientId
output INBOX_STORAGE_ACCOUNT string = inboxStorage.outputs.name
output SERVICE_BUS_NAMESPACE string = serviceBus.outputs.fullyQualifiedNamespace
output KEY_VAULT_URI string = keyVault.outputs.uri
output SEARCH_ENDPOINT string = search.outputs.endpoint
output AUDIT_TABLE_ENDPOINT string = hostStorage.outputs.tableEndpoint
output FUNCTION_APP_NAME string = functionApp.outputs.name
output API_URI string = appService.outputs.apiUri
output LEGACY_URI string = appService.outputs.legacyUri
