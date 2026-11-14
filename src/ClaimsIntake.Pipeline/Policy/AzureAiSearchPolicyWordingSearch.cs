using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Pipeline.Policy;

/// <summary>A clause as it is stored in the Azure AI Search index.</summary>
public sealed record ClauseDocument(
    string Id, string WordingId, string ClauseRef, string Section, string Heading, string Text, ReadOnlyMemory<float> Vector)
{
    /// <summary>Keys may hold letters, digits, <c>_</c>, <c>-</c> and <c>=</c>, so the dot in a clause number becomes a dash.</summary>
    public static string KeyFor(string wordingId, string clauseRef) => $"{wordingId}_{clauseRef.Replace('.', '-')}";
}

/// <summary>The index definition: field names, analysers and the vector profile. Defined in code so it is reviewed and tested like code.</summary>
public static class PolicyWordingIndex
{
    public const string DefaultName = "policy-wordings";
    public const string VectorField = "contentVector";
    public const string VectorProfile = "clause-vector-profile";
    public const string VectorAlgorithm = "clause-hnsw";
    public const string SemanticConfiguration = "clause-semantic";

    /// <param name="indexName">The index name.</param>
    /// <param name="dimensions">The size of the embedding vectors, which the embedding model fixes (1536 for text-embedding-3-small).</param>
    public static SearchIndex Definition(string indexName, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 2);

        return new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
                new SimpleField("wordingId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("clauseRef", SearchFieldDataType.String) { IsSortable = true },
                new SearchableField("section") { IsFilterable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchableField("heading") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchableField("text") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchField(VectorField, SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = dimensions,
                    VectorSearchProfileName = VectorProfile,
                },
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgorithm) },
                Profiles = { new VectorSearchProfile(VectorProfile, VectorAlgorithm) },
            },
        };
    }
}

/// <summary>Runs one hybrid query against the clause index. The seam that keeps the Azure SDK out of the unit tests.</summary>
public interface IClauseIndexQuery
{
    Task<IReadOnlyList<ClauseHit>> QueryAsync(
        string text, ReadOnlyMemory<float> vector, string? wordingId, int top, CancellationToken cancellationToken);
}

/// <summary>Creates the index and uploads clause documents. The seam used by the ingestion tool.</summary>
public interface IClauseIndexWriter
{
    Task EnsureIndexAsync(CancellationToken cancellationToken);

    Task UploadAsync(IReadOnlyList<ClauseDocument> documents, CancellationToken cancellationToken);
}

/// <summary>
/// <c>search_policy_wording</c> on Azure AI Search: one hybrid query, keyword (BM25) and vector together, merged
/// by the service with reciprocal rank fusion. The query is embedded through <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>,
/// so the embedding model is a configuration choice. Compiled and tested against fakes only (ADR 0006).
/// </summary>
public sealed class AzureAiSearchPolicyWordingSearch(
    IClauseIndexQuery index,
    IEmbeddingGenerator<string, Embedding<float>> embeddings) : IPolicyWordingSearch
{
    public async Task<IReadOnlyList<ClauseHit>> SearchAsync(string query, string? wordingId, int top, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        if (wordingId is not null && !WordingIds.IsValid(wordingId))
        {
            throw new ArgumentException("The wording id is not valid.", nameof(wordingId));
        }

        var vector = await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        return await index.QueryAsync(query, vector, wordingId, top, cancellationToken);
    }
}

/// <summary>The real <see cref="IClauseIndexQuery"/>, over a <see cref="SearchClient"/>. Never run against a live service.</summary>
public sealed class SearchClientClauseQuery(SearchClient client) : IClauseIndexQuery
{
    private static readonly string[] SelectedFields = ["wordingId", "clauseRef", "section", "heading", "text"];

    public async Task<IReadOnlyList<ClauseHit>> QueryAsync(
        string text, ReadOnlyMemory<float> vector, string? wordingId, int top, CancellationToken cancellationToken)
    {
        var options = BuildOptions(vector, wordingId, top);
        Response<SearchResults<SearchDocument>> response = await client.SearchAsync<SearchDocument>(text, options, cancellationToken);

        var hits = new List<ClauseHit>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var d = result.Document;
            hits.Add(new ClauseHit(
                d.GetString("wordingId"), d.GetString("clauseRef"), d.GetString("section") ?? "",
                d.GetString("heading") ?? "", d.GetString("text") ?? "", result.Score ?? 0));
        }

        return hits;
    }

    /// <summary>The options of a hybrid query: the text goes to keyword search and the vector to vector search, in one request.</summary>
    internal static SearchOptions BuildOptions(ReadOnlyMemory<float> vector, string? wordingId, int top)
    {
        if (wordingId is not null && !WordingIds.IsValid(wordingId))
        {
            throw new ArgumentException("The wording id is not valid.", nameof(wordingId)); // Never build a filter from free text.
        }

        var options = new SearchOptions
        {
            Size = top,
            Filter = wordingId is null ? null : $"wordingId eq '{wordingId}'",
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(vector)
                    {
                        KNearestNeighborsCount = Math.Max(top * 5, 25),
                        Fields = { PolicyWordingIndex.VectorField },
                    },
                },
            },
        };

        foreach (var field in SelectedFields)
        {
            options.Select.Add(field);
        }

        return options;
    }
}

/// <summary>The real <see cref="IClauseIndexWriter"/>, over the index and search clients. Never run against a live service.</summary>
public sealed class SearchIndexClauseWriter(SearchIndexClient indexClient, SearchClient searchClient, string indexName, int dimensions)
    : IClauseIndexWriter
{
    public async Task EnsureIndexAsync(CancellationToken cancellationToken) =>
        await indexClient.CreateOrUpdateIndexAsync(PolicyWordingIndex.Definition(indexName, dimensions), cancellationToken: cancellationToken);

    public async Task UploadAsync(IReadOnlyList<ClauseDocument> documents, CancellationToken cancellationToken)
    {
        var batch = documents.Select(d => new SearchDocument
        {
            ["id"] = d.Id,
            ["wordingId"] = d.WordingId,
            ["clauseRef"] = d.ClauseRef,
            ["section"] = d.Section,
            ["heading"] = d.Heading,
            ["text"] = d.Text,
            [PolicyWordingIndex.VectorField] = d.Vector.ToArray(),
        }).ToList();

        // Upload by key, so running the ingestion twice updates the clauses instead of duplicating them.
        await searchClient.MergeOrUploadDocumentsAsync(batch, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Chunks the wordings by clause, embeds each clause and uploads it. Used by the <c>ClaimsIntake.Ingestion</c>
/// console tool. Safe to run again: documents are keyed by wording and clause.
/// </summary>
public sealed class WordingIngestor(IEmbeddingGenerator<string, Embedding<float>> embeddings, IClauseIndexWriter writer)
{
    public const int BatchSize = 16;

    /// <returns>The number of clauses uploaded.</returns>
    public async Task<int> IngestAsync(IReadOnlyList<PolicyWording> wordings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wordings);
        var clauses = wordings.SelectMany(w => w.Clauses).ToList();
        await writer.EnsureIndexAsync(cancellationToken);

        foreach (var batch in clauses.Chunk(BatchSize))
        {
            // The heading goes in with the text: "Flood. We will not pay..." embeds closer to a question about flooding.
            var generated = await embeddings.GenerateAsync(batch.Select(c => $"{c.Heading}. {c.Text}").ToList(), cancellationToken: cancellationToken);
            if (generated.Count != batch.Length)
            {
                throw new InvalidOperationException("The embedding model returned a different number of vectors than clauses.");
            }

            var documents = batch.Select((c, i) => new ClauseDocument(
                ClauseDocument.KeyFor(c.WordingId, c.ClauseRef), c.WordingId, c.ClauseRef, c.Section, c.Heading, c.Text, generated[i].Vector)).ToList();
            await writer.UploadAsync(documents, cancellationToken);
        }

        return clauses.Count;
    }
}
