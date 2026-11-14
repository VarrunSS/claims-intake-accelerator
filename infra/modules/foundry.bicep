// Azure AI Foundry resource (kind AIServices) with model deployments. The role that lets the Function's identity call it is in
// modules/roles.bicep (Cognitive Services OpenAI User only: the broader Cognitive Services User role is not needed, INF-01).
//
// Compiles and lints in CI (and with a local Bicep CLI), but has never been deployed. The resource shapes follow the
// Microsoft.CognitiveServices ARM reference; expect to fix property names or API versions on the first real deployment.
// See docs/runbook-model-comparison.md.
//
// There is no key anywhere: local authentication is disabled, so callers must use Entra ID (DefaultAzureCredential).
// A Foundry project is not created here: the comparison and the pipeline only need the account endpoint and deployments.

param name string
param location string
param tags object

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

output name string = account.name
output id string = account.id
@description('Endpoint for the Azure OpenAI client (Intake__Foundry__Endpoint).')
output openAiEndpoint string = 'https://${account.name}.openai.azure.com/'
@description('Endpoint for the Foundry model inference API (the azure-ai-inference kind in evals/models.json).')
output inferenceEndpoint string = 'https://${account.name}.services.ai.azure.com/models'
