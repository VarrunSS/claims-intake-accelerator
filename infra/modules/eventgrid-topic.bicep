// Event Grid system topic on the claim document inbox storage account. It publishes BlobCreated events, which the Function's blob
// trigger (Source = EventGrid) listens for. Its own system-assigned identity writes dead-lettered events to a container
// (modules/roles.bicep gives it Blob Data Contributor on that one container). Stage two of the trigger: see eventgrid-subscription.bicep.

param name string
param location string
param tags object

@description('Resource id of the inbox storage account.')
param inboxStorageId string

resource topic 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    source: inboxStorageId
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

output name string = topic.name
output id string = topic.id
output principalId string = topic.identity.principalId
