// An App Service plan with web apps for the review API and the mock legacy claims system.
// Each app runs as its own user-assigned managed identity (INF-01) and takes no keys or connection strings. Sign-in to the apps
// is Microsoft Entra ID (docs/adr/0009-identity-roles-and-segregation-of-duties.md); infra/entra/ holds the app registrations.

param planName string
param apiName string
param legacyName string
param location string
param tags object

@description('Resource id of the review API identity.')
param apiIdentityId string

@description('Client id of the review API identity.')
param apiIdentityClientId string

@description('Resource id of the legacy mock identity. It holds no Azure role; it exists so the app has an identity of its own.')
param legacyIdentityId string

@description('Client id of the legacy mock identity.')
param legacyIdentityClientId string

param serviceBusNamespace string

@description('Base address of the legacy claims system, where approved claims are written.')
param legacyBaseUrl string

@description('Table endpoint of the storage account that holds the audit log, for example https://staudit123.table.core.windows.net.')
param auditTableEndpoint string

@description('Queue the API reads proposed claims from.')
param reviewQueueName string = 'claims-for-review'

param applicationInsightsConnectionString string

@description('Microsoft Entra tenant that issues the tokens both apps accept.')
param entraTenantId string

@description('Application (client) id of the review API app registration (infra/entra/review-api.app-registration.json).')
param reviewApiClientId string

@description('Identifier URI of the review API app registration. The API accepts it as an audience.')
param reviewApiAudience string

@description('Application (client) id of the legacy mock app registration.')
param legacyClientId string

@description('Identifier URI of the legacy mock app registration. The review API asks for a token for it.')
param legacyAudience string

@description('Linux plan size. B1 is enough for the skeleton; use a Premium v3 size for production.')
param planSku string = 'B1'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
    // One instance: the review store is in memory, so a second instance would hold a different set of claims (REL-01, ADR 0001).
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

var commonSiteConfig = {
  linuxFxVersion: 'DOTNETCORE|10.0'
  minTlsVersion: '1.2'
  scmMinTlsVersion: '1.2'
  ftpsState: 'Disabled'
  http20Enabled: true
  alwaysOn: true
  numberOfWorkers: 1
  healthCheckPath: '/health/live'
}

resource api 'Microsoft.Web/sites@2024-04-01' = {
  name: apiName
  location: location
  // The azd-service-name tag must match the service name in azure.yaml.
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: union(commonSiteConfig, {
      appSettings: [
        { name: 'AZURE_CLIENT_ID', value: apiIdentityClientId }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: reviewApiClientId }
        { name: 'AzureAd__Audience', value: reviewApiAudience }
        // An app-only token for the legacy system, from this identity (Claims.Write).
        { name: 'Legacy__BaseUrl', value: legacyBaseUrl }
        { name: 'Legacy__Scope', value: '${legacyAudience}/.default' }
        { name: 'Intake__ServiceBus__FullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'Intake__ServiceBus__QueueName', value: reviewQueueName }
        { name: 'Intake__ManagedIdentityClientId', value: apiIdentityClientId }
        { name: 'Audit__Table__Endpoint', value: auditTableEndpoint }
        { name: 'Audit__Table__Name', value: 'auditlog' }
        // Azure Monitor OpenTelemetry distro, signing in with the identity above (OBS-01).
        { name: 'Telemetry__Exporter', value: 'azuremonitor' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
      ]
    })
  }
}

resource legacy 'Microsoft.Web/sites@2024-04-01' = {
  name: legacyName
  location: location
  tags: union(tags, { 'azd-service-name': 'legacy' })
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${legacyIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: union(commonSiteConfig, {
      appSettings: [
        { name: 'AZURE_CLIENT_ID', value: legacyIdentityClientId }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: legacyClientId }
        { name: 'AzureAd__Audience', value: legacyAudience }
      ]
    })
  }
}

// Turn off username and password publishing so deployments must use Entra ID (INF-04).
resource apiFtpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: api
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource apiScmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: api
  name: 'scm'
  properties: {
    allow: false
  }
}

resource legacyFtpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: legacy
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource legacyScmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: legacy
  name: 'scm'
  properties: {
    allow: false
  }
}

output apiName string = api.name
output apiUri string = 'https://${api.properties.defaultHostName}'
output legacyName string = legacy.name
output legacyUri string = 'https://${legacy.properties.defaultHostName}'
