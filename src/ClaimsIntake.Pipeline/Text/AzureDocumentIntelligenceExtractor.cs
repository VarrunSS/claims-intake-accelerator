using Azure;
using Azure.AI.DocumentIntelligence;

namespace ClaimsIntake.Pipeline.Text;

/// <summary>Runs layout analysis and returns the text of each page. A seam so the extractor can be tested without Azure.</summary>
public interface IDocumentLayoutAnalyzer
{
    Task<IReadOnlyList<string>> AnalyzePagesAsync(BinaryData content, CancellationToken cancellationToken);
}

/// <summary>
/// Reads documents, including scans, with Azure AI Document Intelligence and its prebuilt-layout model.
/// Authentication is by <c>DefaultAzureCredential</c> (a managed identity in Azure), set up in
/// <see cref="ServiceCollectionExtensions"/>. Compiled and unit-tested against a fake analyser only;
/// it has not been run against the service.
/// </summary>
public sealed class AzureDocumentIntelligenceExtractor(IDocumentLayoutAnalyzer analyzer) : IDocumentTextExtractor
{
    public async Task<DocumentText> ExtractAsync(IntakeDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        IReadOnlyList<string> pages;
        try
        {
            pages = await analyzer.AnalyzePagesAsync(document.Content, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 415)
        {
            throw new UnreadableDocumentException($"Document Intelligence refused the document (HTTP {ex.Status}).", ex);
        }

        var text = new DocumentText(pages);
        if (text.IsEmpty)
        {
            throw new UnreadableDocumentException("Document Intelligence found no text in the document.") { Kind = UnreadableKind.NoText };
        }

        return text;
    }
}

/// <summary>The real analyser, backed by <see cref="DocumentIntelligenceClient"/> and the prebuilt-layout model.</summary>
public sealed class DocumentIntelligenceLayoutAnalyzer(DocumentIntelligenceClient client) : IDocumentLayoutAnalyzer
{
    public const string ModelId = "prebuilt-layout";

    public async Task<IReadOnlyList<string>> AnalyzePagesAsync(BinaryData content, CancellationToken cancellationToken)
    {
        var operation = await client.AnalyzeDocumentAsync(
            WaitUntil.Completed, ModelId, content, cancellationToken: cancellationToken);

        return [.. operation.Value.Pages.Select(page => string.Join('\n', page.Lines.Select(line => line.Content)))];
    }
}
