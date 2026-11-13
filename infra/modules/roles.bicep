// Role assignments for the shared managed identity. Each one is scoped to a single resource, not the
// resource group, and uses a built-in role. Role ids are from Azure built-in roles documentation.

@description('Object (principal) id of the user-assigned managed identity.')
param principalId string

param hostStorageName string
param inboxStorageName string
param serviceBusName string
param keyVaultName string
param applicationInsightsName string

var roleIds = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: hostStorageName
}

resource inboxStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: inboxStorageName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

// Function host: state, deployment package and the internal queues and tables the host uses.
resource hostBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, principalId, roleIds.storageBlobDataOwner)
  scope: hostStorage
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataOwner)
  }
}

resource hostQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, principalId, roleIds.storageQueueDataContributor)
  scope: hostStorage
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageQueueDataContributor)
  }
}

resource hostTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, principalId, roleIds.storageTableDataContributor)
  scope: hostStorage
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageTableDataContributor)
  }
}

// The blob trigger needs Storage Blob Data Owner on the account it watches.
resource inboxBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(inboxStorage.id, principalId, roleIds.storageBlobDataOwner)
  scope: inboxStorage
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataOwner)
  }
}

resource busSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, principalId, roleIds.serviceBusDataSender)
  scope: serviceBus
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.serviceBusDataSender)
  }
}

resource busReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, principalId, roleIds.serviceBusDataReceiver)
  scope: serviceBus
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.serviceBusDataReceiver)
  }
}

resource vaultSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, roleIds.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.keyVaultSecretsUser)
  }
}

// Needed once Application Insights ingestion requires Entra ID (Milestone 6).
resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, principalId, roleIds.monitoringMetricsPublisher)
  scope: applicationInsights
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.monitoringMetricsPublisher)
  }
}
