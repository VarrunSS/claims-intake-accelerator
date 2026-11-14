using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Search.Documents;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Prompts;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Quality;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaimsIntake.Pipeline;

/// <summary>
/// Chooses the implementations behind each seam from configuration under <c>Intake</c>:
/// <c>TextExtractor</c> (Local or DocumentIntelligence), <c>ClaimExtractor</c> (Heuristic or Foundry) and
/// <c>Sink</c> (Http or ServiceBus), <c>PolicySearch</c> (Local or AzureAiSearch) and <c>Assessor</c> (Rules, Agent or
/// None). The defaults need no Azure account. The Azure choices authenticate with
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
        var policySearch = Choice(section, "PolicySearch", "Local", "AzureAiSearch");
        var assessor = Choice(section, "Assessor", "Rules", "Agent", "None");

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
                services.AddSingleton<IClaimExtractor>(sp => new FoundryLlmClaimExtractor(
                    sp.GetRequiredService<IChatClient>(), PromptLibrary.Get(PromptLibrary.Extraction, Optional(section, "PromptVersion"))));
                break;
        }

        // One Azure OpenAI client serves the extractor, the agent assessor and the query embeddings.
        var needsChat = claimExtractor == "Foundry" || assessor == "Agent";
        var needsEmbeddings = policySearch == "AzureAiSearch";
        if (needsChat || needsEmbeddings)
        {
            var foundryEndpoint = Endpoint(section, "Foundry:Endpoint", "Intake:ClaimExtractor is Foundry, Intake:Assessor is Agent or Intake:PolicySearch is AzureAiSearch");
            services.AddSingleton(sp => new AzureOpenAIClient(foundryEndpoint, sp.GetRequiredService<TokenCredential>()));
        }

        if (needsChat)
        {
            var deployment = Required(section, "Foundry:Deployment", "Intake:ClaimExtractor is Foundry or Intake:Assessor is Agent");
            services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<AzureOpenAIClient>()
                .GetChatClient(deployment)
                .AsIChatClient()
                .AsBuilder()
                .UseOpenTelemetry(sourceName: PipelineTelemetry.SourceName) // Prompts are not recorded unless EnableSensitiveData is set.
                .Build());
        }

        var legacy = BaseAddress(section, "Legacy:BaseUrl", "http://localhost:5200/");
        services.AddHttpClient<IPolicyLookup, LegacyPolicyLookup>(client => client.BaseAddress = legacy)
            .AddStandardResilienceHandler();

        // check_duplicate reads the legacy system and, when the API's address is known, the review store.
        services.AddHttpClient<LegacyExistingClaims>(client => client.BaseAddress = legacy).AddStandardResilienceHandler();
        var apiAddress = sink == "Http" || Optional(section, "Api:BaseUrl") is not null ? BaseAddress(section, "Api:BaseUrl", "http://localhost:5100/") : null;
        if (apiAddress is not null)
        {
            services.AddHttpClient<ReviewStoreExistingClaims>(client => client.BaseAddress = apiAddress).AddStandardResilienceHandler();
        }

        services.AddSingleton<IExistingClaims>(sp => new CompositeExistingClaims(apiAddress is null
            ? [sp.GetRequiredService<LegacyExistingClaims>()]
            : [sp.GetRequiredService<LegacyExistingClaims>(), sp.GetRequiredService<ReviewStoreExistingClaims>()]));

        switch (policySearch)
        {
            case "Local":
                var wordings = Optional(section, "Wordings:Folder") ?? Path.Combine(AppContext.BaseDirectory, "policy-wordings");
                services.AddSingleton<IPolicyWordingSearch>(_ => new Bm25PolicyWordingSearch(PolicyWordingParser.LoadFolder(wordings)));
                break;
            default:
                var searchEndpoint = Endpoint(section, "AzureAiSearch:Endpoint", "Intake:PolicySearch is AzureAiSearch");
                var indexName = Optional(section, "AzureAiSearch:IndexName") ?? PolicyWordingIndex.DefaultName;
                var embeddingDeployment = Required(section, "Foundry:EmbeddingDeployment", "Intake:PolicySearch is AzureAiSearch");
                services.AddSingleton(sp => new SearchClient(searchEndpoint, indexName, sp.GetRequiredService<TokenCredential>()));
                services.AddSingleton<IClauseIndexQuery>(sp => new SearchClientClauseQuery(sp.GetRequiredService<SearchClient>()));
                services.AddSingleton(sp => sp.GetRequiredService<AzureOpenAIClient>()
                    .GetEmbeddingClient(embeddingDeployment)
                    .AsIEmbeddingGenerator());
                services.AddSingleton<IPolicyWordingSearch, AzureAiSearchPolicyWordingSearch>();
                break;
        }

        switch (assessor)
        {
            case "Rules":
                services.AddSingleton<IClaimAssessor, RuleBasedClaimAssessor>();
                services.AddSingleton<AssessmentRunner>();
                break;
            case "Agent":
                services.AddSingleton<IClaimAssessor>(sp => new AgentClaimAssessor(
                    sp.GetRequiredService<IChatClient>(), PromptLibrary.Get(PromptLibrary.Assessment, Optional(section, "PromptVersion"))));
                services.AddSingleton<AssessmentRunner>();
                break;
        }

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

        // Input-quality checks (ADR 0007). Intake:Quality:MaxDocumentBytes, MinTextCharacters and MinClaimTerms override the defaults.
        var quality = new InputQualityOptions();
        section.GetSection("Quality").Bind(quality);
        services.AddSingleton<IDocumentHashStore, InMemoryDocumentHashStore>();
        services.AddSingleton(sp => InputQualityGate.CreateDefault(quality, sp.GetRequiredService<IDocumentHashStore>()));

        services.AddSingleton(sp => new ClaimIntakePipeline(
            sp.GetRequiredService<IDocumentTextExtractor>(), sp.GetRequiredService<IClaimExtractor>(), sp.GetRequiredService<IPolicyLookup>(),
            sp.GetRequiredService<IClaimSink>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ClaimIntakePipeline>>(), sp.GetService<AssessmentRunner>(), sp.GetRequiredService<InputQualityGate>()));
        return services;
    }

    /// <summary>Adds the folder watcher used in local mode (ADR 0005). Settings are under <c>LocalInbox</c>.</summary>
    public static IServiceCollection AddLocalInboxWatcher(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LocalInboxOptions>(configuration.GetSection(LocalInboxOptions.SectionName));
        services.AddHostedService<LocalInboxWatcher>();
        return services;
    }

    private static string Choice(IConfigurationSection section, string key, string defaultValue, params string[] others)
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

        return others.FirstOrDefault(o => value.Equals(o, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"{SectionName}:{key} must be {string.Join(" or ", new[] { defaultValue }.Concat(others).Select(o => $"\"{o}\""))}, not \"{value}\".");
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
