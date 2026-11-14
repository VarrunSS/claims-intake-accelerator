using System.Collections.Concurrent;
using System.Security.Cryptography;
using ClaimsIntake.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace ClaimsIntake.Pipeline.Quality;

/// <summary>An empty document, or one larger than the limit.</summary>
public sealed class DocumentSizeCheck(int maxBytes) : IInputQualityCheck
{
    public string Name => "document_size";

    public QualityStage Stage => QualityStage.Document;

    public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
    {
        var size = context.Document.Content.ToMemory().Length;
        QualityFinding? finding = size switch
        {
            0 => new(IssueCode.UnreadableDocument, Name, "The document is empty."),
            _ when size > maxBytes => new(IssueCode.DocumentTooLarge, Name,
                $"The document is {size / (1024.0 * 1024.0):F1} MB, which is over the {maxBytes / (1024.0 * 1024.0):F1} MB limit."),
            _ => null,
        };
        return Task.FromResult(finding);
    }
}

/// <summary>
/// A PDF that cannot be opened: password protected, or not a PDF, or damaged. Only the structure is checked, and the
/// message never repeats what the PDF library said, since that can quote content. A PDF with an owner password only
/// still opens and is read normally.
/// </summary>
public sealed class PdfIntegrityCheck : IInputQualityCheck
{
    public string Name => "pdf_integrity";

    public QualityStage Stage => QualityStage.Document;

    public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(context.Document.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<QualityFinding?>(null);
        }

        try
        {
            using var pdf = PdfDocument.Open(context.Document.Content.ToMemory().ToArray());
            _ = pdf.NumberOfPages;
            return Task.FromResult<QualityFinding?>(null);
        }
        catch (PdfDocumentEncryptedException)
        {
            return Task.FromResult<QualityFinding?>(new(IssueCode.ProtectedOrCorruptDocument, Name,
                "The PDF is password protected, so it could not be read. Ask the sender for an unprotected copy."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult<QualityFinding?>(new(IssueCode.ProtectedOrCorruptDocument, Name,
                "The PDF is corrupt or is not a PDF, so it could not be read. Ask the sender to send it again."));
        }
    }
}

/// <summary>Remembers the SHA-256 of every file seen, and the document id it first arrived as.</summary>
public interface IDocumentHashStore
{
    /// <summary>Records the hash for this document. Returns the id of an earlier document with the same hash, or null when this is the first.</summary>
    string? RegisterOrFindEarlier(string hash, string documentId);
}

/// <summary>
/// In memory, so it forgets on restart and is not shared between instances. A durable store (Table Storage keyed by hash)
/// is the production version; the seam is here so it is a replacement and not a rewrite.
/// </summary>
public sealed class InMemoryDocumentHashStore : IDocumentHashStore
{
    private readonly ConcurrentDictionary<string, string> _seen = new(StringComparer.Ordinal);

    public string? RegisterOrFindEarlier(string hash, string documentId)
    {
        var first = _seen.GetOrAdd(hash, documentId);
        return string.Equals(first, documentId, StringComparison.Ordinal) ? null : first;
    }
}

/// <summary>
/// The same file received before under another name: a re-upload or a forwarded copy. Delivering one document twice
/// under the same id (a retry) is not a duplicate. Different files about the same loss are caught later by the
/// <c>check_duplicate</c> tool, which compares policy, loss date and amount.
/// </summary>
public sealed class DuplicateFileCheck(IDocumentHashStore store) : IInputQualityCheck
{
    public string Name => "duplicate_file";

    public QualityStage Stage => QualityStage.Document;

    public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(context.Document.Content.ToMemory().Span));
        var earlier = store.RegisterOrFindEarlier(hash, context.Document.DocumentId);
        QualityFinding? finding = earlier is null
            ? null
            : new(IssueCode.DuplicateDocument, Name,
                $"This file is identical to one already received ({ClaimIntakePipeline.Sanitise(earlier)}).");
        return Task.FromResult(finding);
    }
}
