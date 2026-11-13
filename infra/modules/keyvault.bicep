// Key Vault using Azure RBAC rather than access policies. No secrets are stored in Milestone 1;
// the vault is here so later milestones have a place for any secret that cannot be avoided.

param name string
param location string
param tags object

@description('Purge protection cannot be turned off once it is on. Leave it on outside throwaway environments.')
param enablePurgeProtection bool = true

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // The property must be omitted, not set to false, when purge protection is off.
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
  }
}

output name string = vault.name
output id string = vault.id
output uri string = vault.properties.vaultUri
