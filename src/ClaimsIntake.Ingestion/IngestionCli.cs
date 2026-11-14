using Azure;
using Azure.Core;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using ClaimsIntake.Pipeline.Policy;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Ingestion;

/// <summary>What the ingestion tool was asked to do.</summary>
public sealed record IngestionOptions(
    string WordingsFolder,
    bool DryRun,
    Uri? SearchEndpoint,
    string IndexName,
    Uri? OpenAiEndpoint,
    string? EmbeddingDeployment,
    int Dimensions,
    string? ManagedIdentityClientId);

/// <summary>
/// Chunks the policy wordings by clause and loads them into the Azure AI Search index, with an embedding for each
/// clause. <c>--dry-run</c> only chunks and prints, and needs nothing from Azure. The upload path authenticates with
/// <c>DefaultAzureCredential</c> (sign in with <c>az login</c>, or run it under a managed identity) and holds no keys.
/// The upload has been compiled and tested against fakes; it has never run against a real service.
/// </summary>
public static class IngestionCli
{
    public const string Usage = """
        Usage: ClaimsIntake.Ingestion --wordings <folder> [--dry-run]
               ClaimsIntake.Ingestion --wordings <folder> --search-endpoint <https url> --openai-endpoint <https url>
                   --embedding-deployment <name> [--index policy-wordings] [--dimensions 1536] [--client-id <guid>]

        --dry-run   chunk the wordings and print what would be uploaded; no network, no sign-in
        Authentication is DefaultAzureCredential. No keys are accepted.
        """;

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "--wordings", "--search-endpoint", "--openai-endpoint", "--embedding-deployment", "--index", "--dimensions", "--client-id",
    };

    public static IngestionOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var dryRun = false;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--dry-run")
            {
                dryRun = true;
            }
            else if (Known.Contains(name) && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[name] = args[++i];
            }
            else
            {
                throw new ArgumentException($"Unknown or incomplete argument \"{name}\".");
            }
        }

        var folder = values.GetValueOrDefault("--wordings") ?? throw new ArgumentException("--wordings <folder> is required.");
        var options = new IngestionOptions(
            folder, dryRun,
            Https(values.GetValueOrDefault("--search-endpoint"), "--search-endpoint"),
            values.GetValueOrDefault("--index") ?? PolicyWordingIndex.DefaultName,
            Https(values.GetValueOrDefault("--openai-endpoint"), "--openai-endpoint"),
            values.GetValueOrDefault("--embedding-deployment"),
            values.TryGetValue("--dimensions", out var d) ? int.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : 1536,
            values.GetValueOrDefault("--client-id"));

        if (!dryRun && (options.SearchEndpoint is null || options.OpenAiEndpoint is null || string.IsNullOrWhiteSpace(options.EmbeddingDeployment)))
        {
            throw new ArgumentException("--search-endpoint, --openai-endpoint and --embedding-deployment are required unless --dry-run is set.");
        }

        return options;
    }

    /// <summary>Runs the tool. Pass fakes for <paramref name="embeddings"/> and <paramref name="writer"/> to test the upload path.</summary>
    public static async Task<int> RunAsync(
        IngestionOptions options,
        TextWriter output,
        CancellationToken cancellationToken,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
        IClauseIndexWriter? writer = null)
    {
        var wordings = PolicyWordingParser.LoadFolder(options.WordingsFolder);
        var clauses = wordings.Sum(w => w.Clauses.Count);

        foreach (var wording in wordings)
        {
            await output.WriteLineAsync($"{wording.Id}: {wording.Clauses.Count} clauses ({string.Join(", ", wording.Clauses.Select(c => c.ClauseRef))})");
        }

        if (options.DryRun)
        {
            await output.WriteLineAsync($"Dry run: {clauses} clauses in {wordings.Count} wordings would be uploaded to index \"{options.IndexName}\". Nothing was sent.");
            return 0;
        }

        if (embeddings is null || writer is null)
        {
            TokenCredential credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = options.ManagedIdentityClientId });
            embeddings ??= new AzureOpenAIClient(options.OpenAiEndpoint!, credential)
                .GetEmbeddingClient(options.EmbeddingDeployment!).AsIEmbeddingGenerator();
            writer ??= new SearchIndexClauseWriter(
                new SearchIndexClient(options.SearchEndpoint!, credential),
                new SearchClient(options.SearchEndpoint!, options.IndexName, credential),
                options.IndexName, options.Dimensions);
        }

        var uploaded = await new WordingIngestor(embeddings, writer).IngestAsync(wordings, cancellationToken);
        await output.WriteLineAsync($"Uploaded {uploaded} clauses to index \"{options.IndexName}\".");
        return 0;
    }

    private static Uri? Https(string? value, string name) =>
        value is null
            ? null
            : Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : throw new ArgumentException($"{name} must be an https URL.");
}
