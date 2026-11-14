// An App Service plan with web apps for the review API and the mock legacy claims system.
// Both run as the shared user-assigned managed identity and take no keys or connection strings.

param planName string
param apiName string
param legacyName string
param location string
param tags object

@description('Resource id of the user-assigned managed identity.')
param identityId string

@description('Client id of the same identity.')
param identityClientId string

param serviceBusNamespace string
param keyVaultUri string

@description('Base address of the legacy claims system, where approved claims are written.')
param legacyBaseUrl string

@description('Table endpoint of the storage account that holds the audit log, for example https://sthost123.table.core.windows.net.')
param auditTableEndpoint string

@description('Queue the API reads proposed claims from.')
param reviewQueueName string = 'claims-for-review'
param applicationInsightsConnectionString string

@description('Linux plan size. B1 is enough for the skeleton; use a Premium v3 size for production.')
param planSku string = 'B1'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
  }
  properties: {
    reserved: true
  }
}

var commonSiteConfig = {
  linuxFxVersion: 'DOTNETCORE|10.0'
  minTlsVersion: '1.2'
  ftpsState: 'Disabled'
  http20Enabled: true
  alwaysOn: true
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
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
    siteConfig: union(commonSiteConfig, {
      appSettings: [
        { name: 'AZURE_CLIENT_ID', value: identityClientId }
        { name: 'ServiceBus__fullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'KeyVault__Uri', value: keyVaultUri }
        { name: 'Legacy__BaseUrl', value: legacyBaseUrl }
        { name: 'Intake__ServiceBus__FullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'Intake__ServiceBus__QueueName', value: reviewQueueName }
        { name: 'Intake__ManagedIdentityClientId', value: identityClientId }
        { name: 'Audit__Table__Endpoint', value: auditTableEndpoint }
        { name: 'Audit__Table__Name', value: 'auditlog' }
        { name: 'Telemetry__Exporter', value: 'console' }
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
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: union(commonSiteConfig, {
      appSettings: [
        { name: 'AZURE_CLIENT_ID', value: identityClientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
      ]
    })
  }
}

// Turn off username and password publishing so deployments must use Entra ID.
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
