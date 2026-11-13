// Service Bus namespace with local (key) authentication disabled. Callers use Entra ID.

param name string
param location string
param tags object

@description('Queue the Function App writes extraction requests to.')
param documentsQueueName string = 'claim-documents'

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

resource documentsQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: documentsQueueName
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P1D'
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
  }
}

output name string = namespace.name
output id string = namespace.id
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output documentsQueueName string = documentsQueue.name
