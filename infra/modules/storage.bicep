// A storage account with shared-key access switched off, so only Entra ID callers can use it.
// Used twice: once for the claim document inbox and once for the Function App host and deployment package.

@description('Globally unique name: 3 to 24 lower-case letters and digits.')
@minLength(3)
@maxLength(24)
param name string

param location string
param tags object

@allowed([
  'Standard_LRS'
  'Standard_ZRS'
])
param skuName string = 'Standard_LRS'

@description('Blob containers to create.')
param containerNames array = []

@description('Tables to create. Used for the audit log of agent tool calls (Milestone 3).')
param tableNames array = []

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: skuName
  }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // Private endpoints and a closed firewall are planned for Milestone 6.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for containerName in containerNames: {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}]

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [for tableName in tableNames: {
  parent: tableService
  name: tableName
}]

output name string = storage.name
output id string = storage.id
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output tableEndpoint string = storage.properties.primaryEndpoints.table
