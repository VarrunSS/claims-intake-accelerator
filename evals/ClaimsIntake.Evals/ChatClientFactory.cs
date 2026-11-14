using Anthropic.Foundry;
using Azure.AI.Inference;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Evals;

/// <summary>
/// Builds the <see cref="IChatClient"/> for a registry entry. Every kind authenticates with <c>DefaultAzureCredential</c>
/// (your <c>az login</c> locally, a managed identity in Azure): there is no key setting anywhere. Never run against a real
/// service from this repository's environment, which has no credentials; construction is unit-tested only.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(ModelEntry model, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Enabled)
        {
            throw new InvalidOperationException(
                $"Model \"{model.Id}\" is not enabled in models.json: it has not been deployed and filled in yet. See docs/runbook-model-comparison.md.");
        }

        credential ??= new DefaultAzureCredential();
        return model.Kind switch
        {
            // Azure OpenAI deployments (GPT family), and any chat-completions model on an Azure OpenAI endpoint.
            "azure-openai" => new AzureOpenAIClient(new Uri(model.Endpoint), credential).GetChatClient(model.Deployment).AsIChatClient(),

            // Azure AI Foundry model inference endpoint (Phi, Mistral and other catalogue models). The packages are still beta/preview.
            "azure-ai-inference" => new ChatCompletionsClient(new Uri(model.Endpoint), credential).AsIChatClient(model.Deployment),

            // Claude on Azure AI Foundry uses the Anthropic Messages API, not chat completions. Endpoint holds the Foundry resource name.
            "foundry-anthropic" => new AnthropicFoundryClient(new AnthropicFoundryIdentityTokenCredentials(credential, model.Endpoint, null)).AsIChatClient(model.Deployment),

            _ => throw new NotSupportedException($"Model kind \"{model.Kind}\" is not supported."),
        };
    }
}
