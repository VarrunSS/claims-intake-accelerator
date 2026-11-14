// Every Azure role assignment of the system, in one place so the role matrix can be read, reviewed and asserted (INF-01).
// Each assignment uses a built-in role and the narrowest scope the resource allows: a queue, a container or a single account, never
// the resource group. scripts/role_matrix.py compiles this to ARM and fails CI when the assignments differ from
// infra/role-matrix.expected.json, so adding Sender to the API, or any role to the legacy mock, is a red build.
//
// The legacy mock has no row: it holds no Azure role at all. What it may do is decided by Entra ID app roles (infra/entra/).
// Role ids are from the Azure built-in roles documentation.

@description('Principal (object) id of the Function App identity.')
param functionPrincipalId string

@description('Principal (object) id of the review API identity.')
param apiPrincipalId string

@description('Principal (object) id of the Event Grid system topic identity. Empty until the topic exists.')
param eventGridPrincipalId string = ''

param hostStorageName string
param inboxStorageName string
param auditStorageName string
param inboxContainerName string
param eventGridDeadLetterContainerName string
param serviceBusName string
param reviewQueueName string
param applicationInsightsName string

@description('Name of the Azure AI Search service that holds the policy wording index.')
param searchServiceName string

@description('Name of the Foundry account. Empty when none is deployed.')
param foundryAccountName string = ''

var roleIds = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
  searchIndexDataReader: '1407120a-92aa-4202-b7e9-c0e197c71c8f'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
}

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: hostStorageName
}

resource hostBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: hostStorage
  name: 'default'
}

resource eventGridDeadLetterContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: hostBlobService
  name: eventGridDeadLetterContainerName
}

resource inboxStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: inboxStorageName
}

resource inboxBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: inboxStorage
  name: 'default'
}

resource inboxContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: inboxBlobService
  name: inboxContainerName
}

resource auditStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: auditStorageName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusName
}

resource reviewQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = {
  parent: serviceBus
  name: reviewQueueName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

resource searchService 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: searchServiceName
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (!empty(foundryAccountName)) {
  name: foundryAccountName
}

// ---------------------------------------------------------------------------------------------------------------------
// Function App identity
// ---------------------------------------------------------------------------------------------------------------------

// Function host: state, the deployment package (Flex Consumption) and the internal blobs, queues and tables the host uses.
// Blob Data Owner is what the host needs on its own account; Table Data Contributor is for the host's diagnostic events. The audit
// table is NOT on this account, so these roles do not reach it.
resource functionHostBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionPrincipalId, roleIds.storageBlobDataOwner)
  scope: hostStorage
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataOwner)
  }
}

resource functionHostQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionPrincipalId, roleIds.storageQueueDataContributor)
  scope: hostStorage
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageQueueDataContributor)
  }
}

resource functionHostTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionPrincipalId, roleIds.storageTableDataContributor)
  scope: hostStorage
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageTableDataContributor)
  }
}

// The blob trigger. Microsoft documents Storage Blob Data Owner for it; it is scoped to the inbox container, not the account.
// Whether that is enough at run time (the Event Grid source needs no listing) is to be confirmed on the first deployment.
resource functionInboxBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(inboxContainer.id, functionPrincipalId, roleIds.storageBlobDataOwner)
  scope: inboxContainer
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataOwner)
  }
}

// Sender on the review queue only: it cannot receive, and it cannot send anywhere else.
resource functionQueueSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(reviewQueue.id, functionPrincipalId, roleIds.serviceBusDataSender)
  scope: reviewQueue
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.serviceBusDataSender)
  }
}

// The pipeline queries the policy wording index (search_policy_wording). Read-only: loading the index is done by the
// ingestion tool under a developer's own sign-in, which needs Search Service Contributor and Search Index Data Contributor.
resource functionSearchReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, functionPrincipalId, roleIds.searchIndexDataReader)
  scope: searchService
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.searchIndexDataReader)
  }
}

// Chat and embeddings through the Azure OpenAI data plane. The broader Cognitive Services User role is not given: the Claude
// client (foundry-anthropic) is used by the evaluation tool only, under a developer's sign-in.
resource functionOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(foundryAccountName)) {
  name: guid(foundryAccount.id, functionPrincipalId, roleIds.cognitiveServicesOpenAIUser)
  scope: foundryAccount
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.cognitiveServicesOpenAIUser)
  }
}

// Application Insights ingestion needs Entra ID (DisableLocalAuth).
resource functionMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, functionPrincipalId, roleIds.monitoringMetricsPublisher)
  scope: applicationInsights
  properties: {
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.monitoringMetricsPublisher)
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Review API identity
// ---------------------------------------------------------------------------------------------------------------------

// Receiver on the review queue only: it cannot send, so a compromised API cannot forge a ProposedClaim message.
resource apiQueueReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(reviewQueue.id, apiPrincipalId, roleIds.serviceBusDataReceiver)
  scope: reviewQueue
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.serviceBusDataReceiver)
  }
}

// The audit table, on an account of its own, so this role reaches nothing else.
resource apiAuditTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(auditStorage.id, apiPrincipalId, roleIds.storageTableDataContributor)
  scope: auditStorage
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageTableDataContributor)
  }
}

resource apiMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, apiPrincipalId, roleIds.monitoringMetricsPublisher)
  scope: applicationInsights
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.monitoringMetricsPublisher)
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Event Grid system topic identity (second stage, once the Function code is deployed)
// ---------------------------------------------------------------------------------------------------------------------

// Writes dead-lettered events to one container, nothing else.
resource eventGridDeadLetter 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(eventGridPrincipalId)) {
  name: guid(eventGridDeadLetterContainer.id, eventGridPrincipalId, roleIds.storageBlobDataContributor)
  scope: eventGridDeadLetterContainer
  properties: {
    principalId: eventGridPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataContributor)
  }
}
