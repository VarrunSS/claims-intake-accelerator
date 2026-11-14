// A storage account with shared-key access switched off, so only Entra ID callers can use it.
// Used three times: the claim document inbox, the Function App host and deployment package, and the audit log table. Separate
// accounts keep the roles apart: a role on an account is the narrowest scope a table can have, so the audit table has its own.

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

@description('Tables to create. Used for the audit log (Milestones 3 and 6).')
param tableNames array = []

@description('Delete blobs in lifecycleContainerName this many days after they were last modified. 0 leaves the policy out. See docs/adr/0010-retention.md.')
@minValue(0)
@maxValue(3650)
param lifecycleDeleteAfterDays int = 0

@description('The container the lifecycle policy applies to.')
param lifecycleContainerName string = 'inbox'

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
    // Private endpoints and a closed firewall are deferred: docs/adr/0011-network.md.
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

// Storage limitation (UK GDPR, PII-03): documents do not stay in the inbox for ever. The original of record is the claims system; the
// inbox is a working copy. Soft delete (above) then keeps a deleted blob recoverable for 7 days.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = if (lifecycleDeleteAfterDays > 0) {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-${lifecycleContainerName}-after-${lifecycleDeleteAfterDays}-days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                '${lifecycleContainerName}/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: lifecycleDeleteAfterDays
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: lifecycleDeleteAfterDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

output name string = storage.name
output id string = storage.id
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output tableEndpoint string = storage.properties.primaryEndpoints.table
