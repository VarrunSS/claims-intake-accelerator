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

@description('Base address of the review API, for the duplicate check (GET /claims/history).')
param apiBaseUrl string

@description('Endpoint of the Azure AI Search service that holds the policy wording index.')
param searchEndpoint string

@description('Queue the extracted claims are handed to for review.')
param reviewQueueName string = 'claims-for-review'
param applicationInsightsConnectionString string

@allowed([
  'Heuristic'
  'Foundry'
])
@description('Intake:ClaimExtractor. Foundry calls the model deployment below through the identity.')
param claimExtractor string = 'Heuristic'

@allowed([
  'Rules'
  'Agent'
])
@description('Intake:Assessor. Agent runs the Agent Framework assessor on the model deployment below.')
param assessor string = 'Rules'

@description('Intake:PromptVersion, the prompts/ version used by the model-backed stages.')
param promptVersion string = 'v1'

@description('Azure OpenAI endpoint of the Foundry account, empty when none is deployed.')
param foundryEndpoint string = ''

@description('Name of the model deployment the extractor and the agent use, empty when none is deployed.')
param foundryDeployment string = ''

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
        { name: 'Intake__Api__BaseUrl', value: apiBaseUrl }
        // Assessment (Milestone 3). The rule-based assessor and the local BM25 search run inside the app and need no
        // other resource. To use Azure AI Search, run the ingestion tool, deploy an embedding model, then set
        // Intake__PolicySearch to AzureAiSearch and Intake__Foundry__Endpoint / EmbeddingDeployment. The agent
        // assessor (Intake__Assessor = Agent) also needs Intake__Foundry__Deployment. Neither model is in this template.
        { name: 'Intake__Assessor', value: assessor }
        // Model-backed stages (Milestone 5). The defaults keep both off. Turn them on with the claimExtractor and assessor
        // parameters; the identity's role on the Foundry account comes from modules/foundry.bicep.
        { name: 'Intake__ClaimExtractor', value: claimExtractor }
        { name: 'Intake__PromptVersion', value: promptVersion }
        { name: 'Intake__Foundry__Endpoint', value: foundryEndpoint }
        { name: 'Intake__Foundry__Deployment', value: foundryDeployment }
        { name: 'Intake__PolicySearch', value: 'Local' }
        { name: 'Intake__AzureAiSearch__Endpoint', value: searchEndpoint }
        { name: 'Intake__AzureAiSearch__IndexName', value: 'policy-wordings' }
        { name: 'AZURE_CLIENT_ID', value: identityClientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
      ]
    }
  }
}

output name string = functionApp.name
output id string = functionApp.id
