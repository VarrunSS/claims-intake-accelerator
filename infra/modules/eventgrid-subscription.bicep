// The event subscription that wakes the Function when a document lands in the inbox (REL-06).
//
// It needs the Function's `blobs_extension` system key in the webhook URL, and that key exists only once the Function code has been
// deployed (the blobs extension registers it). So this is stage two: `azd provision`, then `azd deploy`, then provision again with
// deployEventSubscription=true. The key in the URL is an exception to "no keys" (docs/adr/0003-managed-identity-no-keys.md, addendum):
// it is not a credential for any data, only the shared secret that lets Event Grid call this one webhook. It is stored in the
// subscription (anyone who can read the subscription can read it), so rotate it by redeploying this stage after rotating the key.
//
// Events that cannot be delivered are retried for up to a day (10 attempts), then written to a dead-letter container with the topic's
// identity, and an alert fires on dead-lettered events (modules/alerts.bicep). Nothing is silently dropped (ADR 0001).

param systemTopicName string
param functionHostName string
param functionAppId string

@description('Name of the Function the blob trigger is registered as: Host.Functions.<function name>.')
param functionName string = 'ClaimDocumentReceived'

@description('Resource id of the storage account that holds the dead-letter container.')
param deadLetterStorageId string

param deadLetterContainerName string

@description('The container events are filtered to.')
param inboxContainerName string = 'inbox'

resource systemTopic 'Microsoft.EventGrid/systemTopics@2022-06-15' existing = {
  name: systemTopicName
}

var blobsExtensionKey = listKeys('${functionAppId}/host/default', '2023-12-01').systemKeys.blobs_extension

resource subscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = {
  parent: systemTopic
  name: 'inbox-blob-created'
  properties: {
    eventDeliverySchema: 'EventGridSchema'
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: 'https://${functionHostName}/runtime/webhooks/blobs?functionName=Host.Functions.${functionName}&code=${blobsExtensionKey}'
        maxEventsPerBatch: 1
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Storage.BlobCreated'
      ]
      subjectBeginsWith: '/blobServices/default/containers/${inboxContainerName}/'
    }
    retryPolicy: {
      maxDeliveryAttempts: 10
      eventTimeToLiveInMinutes: 1440
    }
    deadLetterWithResourceIdentity: {
      identity: {
        type: 'SystemAssigned'
      }
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: deadLetterStorageId
          blobContainerName: deadLetterContainerName
        }
      }
    }
  }
}

output name string = subscription.name
