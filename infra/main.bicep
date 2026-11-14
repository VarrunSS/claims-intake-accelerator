// Claims Intake Accelerator infrastructure.
//
// Compiled, linted and policy-checked in CI (.github/workflows/ci.yml, job "iac") and, when a Bicep CLI is to hand, locally. It has
// NEVER BEEN DEPLOYED: no Azure subscription was available. What that leaves unproven is in README.md ("What was not verified").
//
// Deploy with `azd up`. Everything lives in UK South (data residency, docs/00-discovery.md). The blob trigger needs a second pass once the
// Function code is deployed: see deployEventSubscription below and docs/runbook-deploy.md.

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

@description('Create the Azure AI Foundry (AIServices) account and its model deployments. Off by default: no model is deployed and nothing here costs anything until it is turned on. See docs/runbook-model-comparison.md.')
param deployFoundry bool = false

@description('Model deployments for the Foundry account (see modules/foundry.bicep for the shape). Names, versions and quota vary by subscription, so none are assumed.')
param foundryDeployments array = []

@allowed([
  'Heuristic'
  'Foundry'
])
@description('Intake:ClaimExtractor in the Function App. Foundry needs deployFoundry and at least one deployment, and uses the first one.')
param claimExtractor string = 'Heuristic'

@allowed([
  'Rules'
  'Agent'
])
@description('Intake:Assessor in the Function App. Agent needs deployFoundry and at least one deployment, and uses the first one.')
param assessor string = 'Rules'

@description('Prompt version for the model-backed stages (prompts/<kind>/<version>.md).')
param promptVersion string = 'v1'

@description('Microsoft Entra tenant that issues the tokens the API and the legacy mock accept. Defaults to the deployment tenant.')
param entraTenantId string = tenant().tenantId

@description('Application (client) id of the review API app registration, created from infra/entra/review-api.app-registration.json. The API does not start without it.')
param reviewApiClientId string

@description('Identifier URI of the review API app registration. Matches the file in infra/entra/.')
param reviewApiAudience string = 'api://claims-intake-review-api'

@description('Application (client) id of the legacy mock app registration, created from infra/entra/legacy.app-registration.json.')
param legacyClientId string

@description('Identifier URI of the legacy mock app registration. Matches the file in infra/entra/.')
param legacyAudience string = 'api://claims-intake-legacy'

@minValue(30)
@maxValue(3650)
@description('Days an original document stays in the inbox before the lifecycle policy deletes it (docs/adr/0010-retention.md).')
param inboxRetentionDays int = 90

@description('Stage two of the blob trigger: the Event Grid system topic and subscription. Leave false for the first `azd provision`, deploy the Function code, then set true and provision again. The subscription URL needs the Function\'s blobs_extension key, which exists only once the code is deployed.')
param deployEventSubscription bool = false

@description('Email address that receives alerts. Empty creates the alert rules with an action group that has no receiver.')
param alertEmailAddress string = ''

var tags = {
  'azd-env-name': environmentName
  workload: 'claims-intake-accelerator'
  customer: 'thames-mutual-fictional'
}

// A short, stable suffix so globally unique names do not clash between environments.
var token = toLower(uniqueString(subscription().id, environmentName, location))

var names = {
  resourceGroup: 'rg-${environmentName}'
  identityFunction: 'id-func-${environmentName}-${take(token, 6)}'
  identityApi: 'id-api-${environmentName}-${take(token, 6)}'
  identityLegacy: 'id-legacy-${environmentName}-${take(token, 6)}'
  logAnalytics: 'log-${environmentName}-${take(token, 6)}'
  applicationInsights: 'appi-${environmentName}-${take(token, 6)}'
  inboxStorage: 'stinbox${take(token, 13)}'
  hostStorage: 'sthost${take(token, 13)}'
  auditStorage: 'staudit${take(token, 13)}'
  search: 'srch-${environmentName}-${take(token, 6)}'
  foundry: 'ais-${environmentName}-${take(token, 6)}'
  serviceBus: 'sb-${environmentName}-${take(token, 6)}'
  keyVault: 'kv-${take(environmentName, 8)}-${take(token, 8)}'
  functionPlan: 'plan-func-${environmentName}-${take(token, 6)}'
  functionApp: 'func-${environmentName}-${take(token, 6)}'
  webPlan: 'plan-web-${environmentName}-${take(token, 6)}'
  api: 'app-api-${environmentName}-${take(token, 6)}'
  legacy: 'app-legacy-${environmentName}-${take(token, 6)}'
  eventGridTopic: 'evgt-${environmentName}-${take(token, 6)}'
}

var inboxContainerName = 'inbox'
var eventGridDeadLetterContainerName = 'eventgrid-deadletter'
var reviewQueueName = 'claims-for-review'
// The legacy mock's address is known from its name, so the Function App and the API can be told it up front.
var legacyBaseUrl = 'https://${names.legacy}.azurewebsites.net'
// The same for the API: the pipeline reads it for the duplicate check.
var apiBaseUrl = 'https://${names.api}.azurewebsites.net'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: names.resourceGroup
  location: location
  tags: tags
}

// One identity per component, so each holds only the roles it needs (modules/roles.bicep, infra/role-matrix.expected.json).
module identityFunction 'modules/identity.bicep' = {
  name: 'identity-function'
  scope: rg
  params: {
    name: names.identityFunction
    location: location
    tags: tags
  }
}

module identityApi 'modules/identity.bicep' = {
  name: 'identity-api'
  scope: rg
  params: {
    name: names.identityApi
    location: location
    tags: tags
  }
}

module identityLegacy 'modules/identity.bicep' = {
  name: 'identity-legacy'
  scope: rg
  params: {
    name: names.identityLegacy
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
      inboxContainerName
    ]
    lifecycleDeleteAfterDays: inboxRetentionDays
    lifecycleContainerName: inboxContainerName
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
      eventGridDeadLetterContainerName
    ]
  }
}

// The audit log has an account of its own, so the API's Table Data Contributor role reaches the audit table and nothing else, and the
// Function's roles on the host account do not reach the audit table at all.
module auditStorage 'modules/storage.bicep' = {
  name: 'audit-storage'
  scope: rg
  params: {
    name: names.auditStorage
    location: location
    tags: tags
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
    reviewQueueName: reviewQueueName
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

module foundry 'modules/foundry.bicep' = if (deployFoundry) {
  name: 'foundry'
  scope: rg
  params: {
    name: names.foundry
    location: location
    tags: tags
    deployments: foundryDeployments
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
    identityId: identityFunction.outputs.id
    identityClientId: identityFunction.outputs.clientId
    hostStorageName: hostStorage.outputs.name
    inboxBlobEndpoint: inboxStorage.outputs.blobEndpoint
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
    reviewQueueName: reviewQueueName
    legacyBaseUrl: legacyBaseUrl
    legacyAudience: legacyAudience
    apiBaseUrl: apiBaseUrl
    reviewApiAudience: reviewApiAudience
    searchEndpoint: search.outputs.endpoint
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    claimExtractor: claimExtractor
    assessor: assessor
    promptVersion: promptVersion
    foundryEndpoint: deployFoundry ? foundry.?outputs.openAiEndpoint ?? '' : ''
    foundryDeployment: deployFoundry && !empty(foundryDeployments) ? foundryDeployments[0].name : ''
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
    apiIdentityId: identityApi.outputs.id
    apiIdentityClientId: identityApi.outputs.clientId
    legacyIdentityId: identityLegacy.outputs.id
    legacyIdentityClientId: identityLegacy.outputs.clientId
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
    reviewQueueName: reviewQueueName
    legacyBaseUrl: legacyBaseUrl
    auditTableEndpoint: auditStorage.outputs.tableEndpoint
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    entraTenantId: entraTenantId
    reviewApiClientId: reviewApiClientId
    reviewApiAudience: reviewApiAudience
    legacyClientId: legacyClientId
    legacyAudience: legacyAudience
  }
}

// Stage two of the blob trigger, part one: the system topic, with an identity for dead-lettering.
module eventGridTopic 'modules/eventgrid-topic.bicep' = if (deployEventSubscription) {
  name: 'eventgrid-topic'
  scope: rg
  params: {
    name: names.eventGridTopic
    location: location
    tags: tags
    inboxStorageId: inboxStorage.outputs.id
  }
}

module roles 'modules/roles.bicep' = {
  name: 'roles'
  scope: rg
  params: {
    functionPrincipalId: identityFunction.outputs.principalId
    apiPrincipalId: identityApi.outputs.principalId
    eventGridPrincipalId: deployEventSubscription ? eventGridTopic.?outputs.principalId ?? '' : ''
    hostStorageName: hostStorage.outputs.name
    inboxStorageName: inboxStorage.outputs.name
    auditStorageName: auditStorage.outputs.name
    inboxContainerName: inboxContainerName
    eventGridDeadLetterContainerName: eventGridDeadLetterContainerName
    serviceBusName: serviceBus.outputs.name
    reviewQueueName: reviewQueueName
    applicationInsightsName: monitoring.outputs.applicationInsightsName
    searchServiceName: search.outputs.name
    foundryAccountName: deployFoundry ? names.foundry : ''
  }
  dependsOn: [
    foundry
  ]
}

// Stage two, part two: the subscription that wakes the Function. It needs the Function code to be deployed (for its key) and the
// topic's identity to have its role on the dead-letter container.
module eventGridSubscription 'modules/eventgrid-subscription.bicep' = if (deployEventSubscription) {
  name: 'eventgrid-subscription'
  scope: rg
  params: {
    systemTopicName: names.eventGridTopic
    functionHostName: functionApp.outputs.defaultHostName
    functionAppId: functionApp.outputs.id
    deadLetterStorageId: hostStorage.outputs.id
    deadLetterContainerName: eventGridDeadLetterContainerName
    inboxContainerName: inboxContainerName
  }
  dependsOn: [
    eventGridTopic
    roles
  ]
}

module alerts 'modules/alerts.bicep' = {
  name: 'alerts'
  scope: rg
  params: {
    location: location
    tags: tags
    applicationInsightsId: monitoring.outputs.applicationInsightsId
    serviceBusId: serviceBus.outputs.id
    reviewQueueName: reviewQueueName
    eventGridTopicId: deployEventSubscription ? eventGridTopic.?outputs.id ?? '' : ''
    alertEmailAddress: alertEmailAddress
  }
}

module workbook 'modules/workbook.bicep' = {
  name: 'workbook'
  scope: rg
  params: {
    location: location
    tags: tags
    applicationInsightsId: monitoring.outputs.applicationInsightsId
  }
}

// azd reads these outputs into the environment.
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_FUNCTION_IDENTITY_CLIENT_ID string = identityFunction.outputs.clientId
output AZURE_API_IDENTITY_CLIENT_ID string = identityApi.outputs.clientId
output AZURE_LEGACY_IDENTITY_CLIENT_ID string = identityLegacy.outputs.clientId
// The principal (object) ids are what infra/entra/assign-app-roles.ps1 grants the Entra app roles to.
output FUNCTION_IDENTITY_PRINCIPAL_ID string = identityFunction.outputs.principalId
output API_IDENTITY_PRINCIPAL_ID string = identityApi.outputs.principalId
output INBOX_STORAGE_ACCOUNT string = inboxStorage.outputs.name
output SERVICE_BUS_NAMESPACE string = serviceBus.outputs.fullyQualifiedNamespace
output KEY_VAULT_URI string = keyVault.outputs.uri
output SEARCH_ENDPOINT string = search.outputs.endpoint
output FOUNDRY_OPENAI_ENDPOINT string = deployFoundry ? foundry.?outputs.openAiEndpoint ?? '' : ''
output FOUNDRY_INFERENCE_ENDPOINT string = deployFoundry ? foundry.?outputs.inferenceEndpoint ?? '' : ''
output AUDIT_TABLE_ENDPOINT string = auditStorage.outputs.tableEndpoint
output FUNCTION_APP_NAME string = functionApp.outputs.name
output API_URI string = appService.outputs.apiUri
output LEGACY_URI string = appService.outputs.legacyUri
