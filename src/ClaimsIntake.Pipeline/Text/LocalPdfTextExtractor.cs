using System.Text;
using ClaimsIntake.Pipeline.Quality;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ClaimsIntake.Pipeline.Text;

/// <summary>
/// Reads text-based PDFs with PdfPig, and plain .txt and .eml files as UTF-8. It does no OCR, so a scanned
/// PDF yields no text and is refused. Use <see cref="AzureDocumentIntelligenceExtractor"/> for scans.
/// </summary>
public sealed class LocalPdfTextExtractor : IDocumentTextExtractor
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".pdf", ".txt", ".eml"];

    public Task<DocumentText> ExtractAsync(IntakeDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var extension = Path.GetExtension(document.FileName).ToLowerInvariant();

        DocumentText text = extension switch
        {
            ".pdf" => ParsedPdfCache.TryTake(document, out var parsed) ? new DocumentText(parsed) : ReadPdf(document.Content),
            ".txt" or ".eml" => new DocumentText([Normalise(Encoding.UTF8.GetString(document.Content.ToMemory().Span))]),
            _ => throw new UnreadableDocumentException(
                $"Files of type \"{extension}\" are not supported. Use one of: {string.Join(", ", SupportedExtensions)}."),
        };

        if (text.IsEmpty)
        {
            throw new UnreadableDocumentException(
                "No text could be read from the document. It may be a scan; use Document Intelligence for scanned files.")
            {
                Kind = UnreadableKind.NoText,
            };
        }

        return Task.FromResult(text);
    }

    private static DocumentText ReadPdf(BinaryData content)
    {
        try
        {
            using var pdf = PdfDocument.Open(content.ToMemory().ToArray());
            var pages = pdf.GetPages().Select(page => Normalise(ContentOrderTextExtractor.GetText(page))).ToList();
            return new DocumentText(pages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The PDF library's message can quote content, so only the type is kept.
            throw new UnreadableDocumentException($"The PDF could not be read ({ex.GetType().Name}).", ex);
        }
    }

    internal static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
