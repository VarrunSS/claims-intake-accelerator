using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaimsIntake.Pipeline;

/// <summary>
/// Chooses the implementations behind each seam from configuration under <c>Intake</c>:
/// <c>TextExtractor</c> (Local or DocumentIntelligence), <c>ClaimExtractor</c> (Heuristic or Foundry) and
/// <c>Sink</c> (Http or ServiceBus). The defaults need no Azure account. The Azure choices authenticate with
/// <see cref="DefaultAzureCredential"/>: a managed identity in Azure, your developer sign-in locally.
/// No keys are read from configuration.
/// </summary>
public static class ServiceCollectionExtensions
{
    public const string SectionName = "Intake";

    public static IServiceCollection AddClaimIntakePipeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);

        var textExtractor = Choice(section, "TextExtractor", "Local", "DocumentIntelligence");
        var claimExtractor = Choice(section, "ClaimExtractor", "Heuristic", "Foundry");
        var sink = Choice(section, "Sink", "Http", "ServiceBus");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential(
            new DefaultAzureCredentialOptions { ManagedIdentityClientId = Optional(section, "ManagedIdentityClientId") }));

        switch (textExtractor)
        {
            case "Local":
                services.AddSingleton<IDocumentTextExtractor, LocalPdfTextExtractor>();
                break;
            default:
                var diEndpoint = Endpoint(section, "DocumentIntelligence:Endpoint", "Intake:TextExtractor is DocumentIntelligence");
                services.AddSingleton(sp => new DocumentIntelligenceClient(diEndpoint, sp.GetRequiredService<TokenCredential>()));
                services.AddSingleton<IDocumentLayoutAnalyzer, DocumentIntelligenceLayoutAnalyzer>();
                services.AddSingleton<IDocumentTextExtractor, AzureDocumentIntelligenceExtractor>();
                break;
        }

        switch (claimExtractor)
        {
            case "Heuristic":
                services.AddSingleton<IClaimExtractor, HeuristicClaimExtractor>();
                break;
            default:
                var foundryEndpoint = Endpoint(section, "Foundry:Endpoint", "Intake:ClaimExtractor is Foundry");
                var deployment = Required(section, "Foundry:Deployment", "Intake:ClaimExtractor is Foundry");
                services.AddSingleton<IChatClient>(sp => new AzureOpenAIClient(foundryEndpoint, sp.GetRequiredService<TokenCredential>())
                    .GetChatClient(deployment)
                    .AsIChatClient()
                    .AsBuilder()
                    .UseOpenTelemetry(sourceName: PipelineTelemetry.SourceName) // Prompts are not recorded unless EnableSensitiveData is set.
                    .Build());
                services.AddSingleton<IClaimExtractor, FoundryLlmClaimExtractor>();
                break;
        }

        var legacy = BaseAddress(section, "Legacy:BaseUrl", "http://localhost:5200/");
        services.AddHttpClient<IPolicyLookup, LegacyPolicyLookup>(client => client.BaseAddress = legacy)
            .AddStandardResilienceHandler();

        switch (sink)
        {
            case "Http":
                var api = BaseAddress(section, "Api:BaseUrl", "http://localhost:5100/");
                // Safe to retry: the API recognises a document it has already seen.
                services.AddHttpClient<IClaimSink, HttpClaimSink>(client => client.BaseAddress = api)
                    .AddStandardResilienceHandler();
                break;
            default:
                var ns = Required(section, "ServiceBus:FullyQualifiedNamespace", "Intake:Sink is ServiceBus");
                var queue = section["ServiceBus:QueueName"] is { Length: > 0 } q ? q : "claims-for-review";
                services.AddSingleton(sp => new ServiceBusClient(ns, sp.GetRequiredService<TokenCredential>()));
                services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(queue));
                services.AddSingleton<IClaimSink, ServiceBusClaimSink>();
                break;
        }

        services.AddSingleton<ClaimIntakePipeline>();
        return services;
    }

    /// <summary>Adds the folder watcher used in local mode (ADR 0005). Settings are under <c>LocalInbox</c>.</summary>
    public static IServiceCollection AddLocalInboxWatcher(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LocalInboxOptions>(configuration.GetSection(LocalInboxOptions.SectionName));
        services.AddHostedService<LocalInboxWatcher>();
        return services;
    }

    private static string Choice(IConfigurationSection section, string key, string defaultValue, string other)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (value.Equals(defaultValue, StringComparison.OrdinalIgnoreCase))
        {
            return defaultValue;
        }

        return value.Equals(other, StringComparison.OrdinalIgnoreCase)
            ? other
            : throw new InvalidOperationException($"{SectionName}:{key} must be \"{defaultValue}\" or \"{other}\", not \"{value}\".");
    }

    private static string? Optional(IConfigurationSection section, string key) =>
        section[key] is { Length: > 0 } value ? value : null;

    private static string Required(IConfigurationSection section, string key, string because) =>
        Optional(section, key) ?? throw new InvalidOperationException($"{SectionName}:{key} is required when {because}.");

    private static Uri Endpoint(IConfigurationSection section, string key, string because)
    {
        var value = Required(section, key, because);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidOperationException($"{SectionName}:{key} must be an https URL.");
    }

    private static Uri BaseAddress(IConfigurationSection section, string key, string defaultValue)
    {
        var value = Optional(section, key) ?? defaultValue;
        if (!value.EndsWith('/'))
        {
            value += "/";
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException($"{SectionName}:{key} must be an http or https URL.");
    }
}
