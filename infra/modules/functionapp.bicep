// Function App on the Flex Consumption plan (Linux, .NET isolated worker).
// Every connection is identity based: the app settings name an account or namespace, never a key.

param planName string
param functionAppName string
param location string
param tags object

@description('Resource id of the user-assigned managed identity.')
param identityId string

@description('Client id of the same identity. Selects it for identity-based connections.')
param identityClientId string

@description('Storage account that holds the host state and the deployment package.')
param hostStorageName string

@description('Container in that account that holds the deployment package.')
param deploymentContainerName string = 'app-package'

@description('Blob endpoint of the claim document inbox account, for example https://stinbox123.blob.core.windows.net/.')
param inboxBlobEndpoint string

param serviceBusNamespace string
param documentsQueueName string

@description('Base address of the legacy claims system, for the policy lookup.')
param legacyBaseUrl string

@description('Queue the extracted claims are handed to for review.')
param reviewQueueName string = 'claims-for-review'
param applicationInsightsConnectionString string

@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

param maximumInstanceCount int = 20

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: hostStorageName
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  // The azd-service-name tag must match the service name in azure.yaml.
  tags: union(tags, { 'azd-service-name': 'functions' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityId
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      // Flex Consumption sets FUNCTIONS_WORKER_RUNTIME itself; do not add it here.
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: hostStorageName }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: identityClientId }
        { name: 'InboxStorage__serviceUri', value: inboxBlobEndpoint }
        { name: 'InboxStorage__credential', value: 'managedidentity' }
        { name: 'InboxStorage__clientId', value: identityClientId }
        { name: 'ServiceBus__fullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'ServiceBus__credential', value: 'managedidentity' }
        { name: 'ServiceBus__clientId', value: identityClientId }
        { name: 'ClaimDocumentsQueue', value: documentsQueueName }
        // Intake pipeline (ClaimsIntake.Pipeline). The Document Intelligence and Foundry settings are not set yet:
        // those resources are not in this template, so the Function App uses the local text and heuristic
        // extractors until they are added. See docs/adr/0005 and the README.
        { name: 'Intake__Sink', value: 'ServiceBus' }
        { name: 'Intake__ServiceBus__FullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'Intake__ServiceBus__QueueName', value: reviewQueueName }
        { name: 'Intake__ManagedIdentityClientId', value: identityClientId }
        { name: 'Intake__Legacy__BaseUrl', value: legacyBaseUrl }
        { name: 'AZURE_CLIENT_ID', value: identityClientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
      ]
    }
  }
}

output name string = functionApp.name
output id string = functionApp.id
