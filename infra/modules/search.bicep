// Azure AI Search for the policy wording index (Milestone 3, ADR 0006).
// Key (API key) authentication is switched off, so callers need an Entra ID role: the identity gets Search Index
// Data Reader in roles.bicep. The index itself is created by the ingestion tool (src/ClaimsIntake.Ingestion), not here.
//
// NOT VALIDATED: this has not been through `az bicep build`, what-if or a deployment.

param name string
param location string
param tags object

@allowed([
  'free'
  'basic'
])
@description('free is enough for the 84 clauses in the sample wordings (3 indexes, 50 MB) and costs nothing, but there can be one free service per subscription, it has no SLA, and it shares capacity. Use basic for anything else (about 70 pounds a month, which is why it is not the default).')
param sku string = 'free'

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    // Entra ID only. With local auth disabled, authOptions must not be set.
    disableLocalAuth: true
    publicNetworkAccess: 'enabled'
    semanticSearch: 'disabled'
  }
}

output name string = search.name
output id string = search.id
output endpoint string = 'https://${search.name}.search.windows.net'
