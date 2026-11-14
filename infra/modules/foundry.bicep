// Azure AI Foundry resource (kind AIServices) with model deployments, and the roles that let the managed identity call it.
//
// NOT VALIDATED. The az and bicep CLIs were not available when this was written, so this module has never been through
// `az bicep build`, what-if or a deployment. The resource shapes follow the Microsoft.CognitiveServices ARM reference
// from memory; expect to fix property names or API versions on the first real deployment. See docs/runbook-model-comparison.md.
//
// There is no key anywhere: local authentication is disabled, so callers must use Entra ID (DefaultAzureCredential).
// A Foundry project is not created here: the comparison and the pipeline only need the account endpoint and deployments.

param name string
param location string
param tags object

@description('Object (principal) id of the user-assigned managed identity that calls the models.')
param principalId string

@description('''
Model deployments to create. Model names, versions, SKUs and quota differ by region and subscription, so none are
assumed: pass what your subscription can deploy in UK South. Each item:
{ name: 'gpt-mid', model: { format: 'OpenAI', name: '<model name>', version: '<version>' }, sku: { name: 'GlobalStandard', capacity: 30 } }
For a Claude model the format is 'Anthropic'; check that it is offered in the region and that the subscription has accepted its terms.
capacity is in thousands of tokens per minute.''')
param deployments array = []

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  properties: {
    // The custom sub-domain is required for Entra ID authentication and gives the endpoint https://<name>.openai.azure.com/.
    customSubDomainName: name
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

// One at a time: the service rejects parallel deployment changes on one account.
@batchSize(1)
resource modelDeployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for d in deployments: {
  parent: account
  name: d.name
  sku: d.sku
  properties: {
    model: d.model
  }
}]

var roleIds = {
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  cognitiveServicesUser: 'a97b65f3-24c7-4388-baec-2e87135dc908'
}

// Chat and embeddings through the Azure OpenAI data plane (the pipeline's AzureOpenAIClient).
resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, roleIds.cognitiveServicesOpenAIUser)
  scope: account
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.cognitiveServicesOpenAIUser)
  }
}

// Model inference and Claude deployments use the broader Cognitive Services data plane.
resource cognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, roleIds.cognitiveServicesUser)
  scope: account
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.cognitiveServicesUser)
  }
}

output name string = account.name
output id string = account.id
@description('Endpoint for the Azure OpenAI client (Intake__Foundry__Endpoint).')
output openAiEndpoint string = 'https://${account.name}.openai.azure.com/'
@description('Endpoint for the Foundry model inference API (the azure-ai-inference kind in evals/models.json).')
output inferenceEndpoint string = 'https://${account.name}.services.ai.azure.com/models'
