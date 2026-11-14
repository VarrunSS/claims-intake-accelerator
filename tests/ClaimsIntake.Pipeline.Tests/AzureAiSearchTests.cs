using ClaimsIntake.Ingestion;
using ClaimsIntake.Pipeline.Policy;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>An embedding model that needs no network: the vector is derived from the text length, so it is stable.</summary>
internal sealed class FakeEmbeddings(int dimensions = 4) : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<IReadOnlyList<string>> Batches { get; } = [];
    public int ReturnWrongCount { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.ToList();
        Batches.Add(list);
        var vectors = list.Select(v => new Embedding<float>(Enumerable.Range(0, dimensions).Select(i => (float)(v.Length + i)).ToArray()));
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(vectors.Skip(ReturnWrongCount)));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class FakeIndexQuery(params ClauseHit[] hits) : IClauseIndexQuery
{
    public List<(string Text, float[] Vector, string? WordingId, int Top)> Calls { get; } = [];

    public Task<IReadOnlyList<ClauseHit>> QueryAsync(string text, ReadOnlyMemory<float> vector, string? wordingId, int top, CancellationToken cancellationToken)
    {
        Calls.Add((text, vector.ToArray(), wordingId, top));
        return Task.FromResult<IReadOnlyList<ClauseHit>>(hits);
    }
}

internal sealed class FakeIndexWriter : IClauseIndexWriter
{
    public int EnsureCalls { get; private set; }
    public List<ClauseDocument> Uploaded { get; } = [];
    public int Batches { get; private set; }

    public Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        EnsureCalls++;
        return Task.CompletedTask;
    }

    public Task UploadAsync(IReadOnlyList<ClauseDocument> documents, CancellationToken cancellationToken)
    {
        Batches++;
        Uploaded.AddRange(documents);
        return Task.CompletedTask;
    }
}

public class AzureAiSearchTests
{
    [Fact]
    public void The_index_definition_has_the_fields_analysers_and_vector_profile_the_queries_rely_on()
    {
        var index = PolicyWordingIndex.Definition("policy-wordings", 1536);

        Assert.Equal("policy-wordings", index.Name);
        var fields = index.Fields.ToDictionary(f => f.Name);
        Assert.Equal(["id", "wordingId", "clauseRef", "section", "heading", "text", "contentVector"], index.Fields.Select(f => f.Name));
        Assert.True(fields["id"].IsKey);
        Assert.True(fields["wordingId"].IsFilterable);
        Assert.True(fields["text"].IsSearchable);
        Assert.Equal("en.microsoft", fields["text"].AnalyzerName.ToString());
        var vector = fields[PolicyWordingIndex.VectorField];
        Assert.Equal(1536, vector.VectorSearchDimensions);
        Assert.Equal(PolicyWordingIndex.VectorProfile, vector.VectorSearchProfileName);
        Assert.Equal(PolicyWordingIndex.VectorProfile, Assert.Single(index.VectorSearch.Profiles).Name);
        Assert.Equal(PolicyWordingIndex.VectorAlgorithm, Assert.Single(index.VectorSearch.Algorithms).Name);
    }

    [Theory]
    [InlineData("", 1536)]
    [InlineData("x", 1)]
    public void An_index_without_a_name_or_dimensions_is_refused(string name, int dimensions)
    {
        Assert.ThrowsAny<ArgumentException>(() => PolicyWordingIndex.Definition(name, dimensions));
    }

    [Fact]
    public void Clause_document_keys_only_use_characters_the_service_allows()
    {
        Assert.Equal("TM-PW-CC-2026_4-1", ClauseDocument.KeyFor("TM-PW-CC-2026", "4.1"));
        Assert.Matches("^[A-Za-z0-9_=-]+$", ClauseDocument.KeyFor("TM-PW-CC-2026", "10.12"));
    }

    [Fact]
    public void A_hybrid_query_sends_the_text_and_the_vector_together_with_a_filter_on_the_wording()
    {
        var options = SearchClientClauseQuery.BuildOptions(new float[] { 1, 2, 3 }, "TM-PW-OF-2026", 5);

        Assert.Equal(5, options.Size);
        Assert.Equal("wordingId eq 'TM-PW-OF-2026'", options.Filter);
        var query = Assert.IsType<Azure.Search.Documents.Models.VectorizedQuery>(Assert.Single(options.VectorSearch.Queries));
        Assert.Equal([1f, 2f, 3f], query.Vector.ToArray());
        Assert.Equal(PolicyWordingIndex.VectorField, Assert.Single(query.Fields));
        Assert.Equal(25, query.KNearestNeighborsCount);
        Assert.Equal(["wordingId", "clauseRef", "section", "heading", "text"], options.Select);
    }

    [Fact]
    public void Without_a_wording_there_is_no_filter()
    {
        Assert.Null(SearchClientClauseQuery.BuildOptions(new float[] { 1 }, null, 3).Filter);
    }

    [Theory]
    [InlineData("TM-PW-OF-2026' or wordingId ne '")]
    [InlineData("a b")]
    [InlineData("")]
    public void A_wording_id_that_could_alter_the_filter_is_refused(string wordingId)
    {
        Assert.Throws<ArgumentException>(() => SearchClientClauseQuery.BuildOptions(new float[] { 1 }, wordingId, 3));
    }

    [Fact]
    public async Task The_search_embeds_the_query_and_passes_text_vector_filter_and_size_to_the_index()
    {
        var hit = new ClauseHit("TM-PW-OF-2026", "4.1", "Exclusions", "Flood", "We will not pay for flood.", 0.03);
        var index = new FakeIndexQuery(hit);
        var embeddings = new FakeEmbeddings();
        var search = new AzureAiSearchPolicyWordingSearch(index, embeddings);

        var hits = await search.SearchAsync("flood", "TM-PW-OF-2026", 3, CancellationToken.None);

        Assert.Equal([hit], hits);
        Assert.Equal(["flood"], Assert.Single(embeddings.Batches));
        var call = Assert.Single(index.Calls);
        Assert.Equal(("flood", "TM-PW-OF-2026", 3), (call.Text, call.WordingId, call.Top));
        Assert.Equal([5f, 6f, 7f, 8f], call.Vector);
    }

    [Fact]
    public async Task The_search_refuses_a_blank_query_a_bad_wording_id_and_a_top_below_one_before_calling_anything()
    {
        var index = new FakeIndexQuery();
        var embeddings = new FakeEmbeddings();
        var search = new AzureAiSearchPolicyWordingSearch(index, embeddings);

        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync(" ", null, 3, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync("flood", "x' or 1 eq 1", 3, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => search.SearchAsync("flood", null, 0, CancellationToken.None));

        Assert.Empty(embeddings.Batches);
        Assert.Empty(index.Calls);
    }

    [Fact]
    public async Task Ingestion_creates_the_index_embeds_each_clause_with_its_heading_and_uploads_in_batches()
    {
        var embeddings = new FakeEmbeddings();
        var writer = new FakeIndexWriter();

        var count = await new WordingIngestor(embeddings, writer).IngestAsync(Corpus.Wordings, CancellationToken.None);

        Assert.Equal(Corpus.Wordings.Sum(w => w.Clauses.Count), count);
        Assert.Equal(1, writer.EnsureCalls);
        Assert.Equal(count, writer.Uploaded.Count);
        Assert.Equal(count, writer.Uploaded.Select(d => d.Id).Distinct().Count());
        Assert.Equal((int)Math.Ceiling(count / (double)WordingIngestor.BatchSize), writer.Batches);
        Assert.All(embeddings.Batches, b => Assert.True(b.Count <= WordingIngestor.BatchSize));
        Assert.StartsWith("Flood. We will not pay", embeddings.Batches.SelectMany(b => b).First(t => t.StartsWith("Flood.", StringComparison.Ordinal)));
        Assert.All(writer.Uploaded, d => Assert.Equal(4, d.Vector.Length));
        var flood = writer.Uploaded.Single(d => d.WordingId == "TM-PW-OF-2026" && d.ClauseRef == "4.1");
        Assert.Equal(("Exclusions", "Flood", "TM-PW-OF-2026_4-1"), (flood.Section, flood.Heading, flood.Id));
    }

    [Fact]
    public async Task Ingestion_stops_if_the_model_returns_the_wrong_number_of_vectors()
    {
        var embeddings = new FakeEmbeddings { ReturnWrongCount = 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WordingIngestor(embeddings, new FakeIndexWriter()).IngestAsync(Corpus.Wordings, CancellationToken.None));
    }

    // ----- the console tool -----

    [Fact]
    public void The_console_tool_needs_a_wordings_folder_and_refuses_unknown_arguments_and_keys()
    {
        Assert.Throws<ArgumentException>(() => IngestionCli.Parse([]));
        Assert.Throws<ArgumentException>(() => IngestionCli.Parse(["--wordings", "x", "--api-key", "secret"]).ToString());
        Assert.Throws<ArgumentException>(() => IngestionCli.Parse(["--wordings", "x", "--dry-run", "--search-endpoint", "http://insecure.example"]));
        Assert.Throws<ArgumentException>(() => IngestionCli.Parse(["--wordings", "x"])); // Not a dry run, and no endpoints.
    }

    [Fact]
    public void The_console_tool_reads_the_azure_options()
    {
        var options = IngestionCli.Parse(["--wordings", "w", "--search-endpoint", "https://s.search.windows.net", "--openai-endpoint", "https://o.openai.azure.com",
            "--embedding-deployment", "emb", "--index", "idx", "--dimensions", "3072"]);

        Assert.Equal(("w", false, "idx", 3072, "emb"), (options.WordingsFolder, options.DryRun, options.IndexName, options.Dimensions, options.EmbeddingDeployment));
        Assert.Equal("s.search.windows.net", options.SearchEndpoint!.Host);
    }

    [Fact]
    public async Task A_dry_run_chunks_the_wordings_and_prints_without_calling_azure()
    {
        var output = new StringWriter();

        var exit = await IngestionCli.RunAsync(IngestionCli.Parse(["--wordings", Corpus.Folder, "--dry-run"]), output, CancellationToken.None);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("TM-PW-CC-2026: 23 clauses", text);
        Assert.Contains("Dry run: 84 clauses in 4 wordings", text);
        Assert.Contains("Nothing was sent", text);
    }

    [Fact]
    public async Task A_real_run_uploads_through_the_embedding_model_and_the_index_writer()
    {
        var writer = new FakeIndexWriter();
        var options = IngestionCli.Parse(["--wordings", Corpus.Folder, "--search-endpoint", "https://s.search.windows.net",
            "--openai-endpoint", "https://o.openai.azure.com", "--embedding-deployment", "emb"]);
        var output = new StringWriter();

        await IngestionCli.RunAsync(options, output, CancellationToken.None, new FakeEmbeddings(), writer);

        Assert.Equal(84, writer.Uploaded.Count);
        Assert.Contains("Uploaded 84 clauses", output.ToString());
    }
}
