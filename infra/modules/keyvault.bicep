// Key Vault using Azure RBAC rather than access policies. It is empty and no identity has a role on it: there is no secret to
// read. It is here so there is somewhere for a secret that cannot be avoided; grant Key Vault Secrets User to the one identity that
// needs it, on that secret, when that day comes (INF-03).

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
