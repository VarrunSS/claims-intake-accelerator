// One user-assigned managed identity shared by the Function App and both web apps in Milestone 1.
// Splitting it per component is planned for Milestone 6 (see docs/adr/0003-managed-identity-no-keys.md).

@description('Name of the identity.')
param name string

param location string
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

output id string = identity.id
output name string = identity.name
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
