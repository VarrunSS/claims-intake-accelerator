// A user-assigned managed identity. main.bicep creates one per component (the Function App, the review API and the legacy
// mock) so that each holds only the roles it needs (INF-01, docs/adr/0003-managed-identity-no-keys.md).

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
