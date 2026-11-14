// Service Bus namespace with local (key) authentication disabled. Callers use Entra ID.
//
// One queue: claims-for-review, from the Function (Sender) to the API (Receiver). The claim-documents queue the first milestone
// provisioned was never used (the Function runs the whole pipeline in process) and is gone (DOC-01 d).
// Public network access stays on because private endpoints need the Premium tier: docs/adr/0011-network.md.

param name string
param location string
param tags object

@description('Queue the API reads extracted claims from.')
param reviewQueueName string = 'claims-for-review'

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource reviewQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: reviewQueueName
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    // The message id is the claim id, so a repeat of the same document within the window is dropped by the queue itself. It can
    // only be set when a queue is created, which is why it is here before the first deployment (REL-07).
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
  }
}

output name string = namespace.name
output id string = namespace.id
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output reviewQueueName string = reviewQueue.name
