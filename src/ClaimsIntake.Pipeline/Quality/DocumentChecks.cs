using System.Collections.Concurrent;
using System.Security.Cryptography;
using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
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
/// The file is what its name says (INP-02). The extension picks the type the rest of the pipeline reads, so the first bytes must
/// match it: <c>%PDF-</c> for a PDF, the PNG, JPEG or TIFF signatures for images, and for .txt and .eml text with no binary
/// signature and no NUL bytes (an .eml must also start with a header line). A ZIP or an executable renamed to .pdf is stopped here,
/// with a reason, instead of reaching the PDF parser or, in Document Intelligence mode, being sent to a service. The message never
/// quotes the file. This is a sniff, not a malware scan: scanning the inbox is Defender for Storage's job (README, known limitations).
/// </summary>
public sealed class FileSignatureCheck : IInputQualityCheck
{
    private static readonly (byte[] Magic, string Kind)[] Binary =
    [
        ("%PDF-"u8.ToArray(), "a PDF"),
        ([(byte)'P', (byte)'K', 0x03, 0x04], "a ZIP archive or an Office document"),
        ("MZ"u8.ToArray(), "a program"),
        ([0x7F, (byte)'E', (byte)'L', (byte)'F'], "a program"),
        ([0x89, (byte)'P', (byte)'N', (byte)'G'], "an image"),
        ("GIF8"u8.ToArray(), "an image"),
        ([0xFF, 0xD8, 0xFF], "an image"),
        ([0xD0, 0xCF, 0x11, 0xE0], "an old Office document"),
        ([0x1F, 0x8B], "a compressed archive"),
        ("Rar!"u8.ToArray(), "a compressed archive"),
        ([0x37, 0x7A, 0xBC, 0xAF], "a compressed archive"),
    ];

    public string Name => "file_signature";

    public QualityStage Stage => QualityStage.Document;

    public Task<QualityFinding?> CheckAsync(InputQualityContext context, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(context.Document.FileName).ToLowerInvariant();
        var bytes = context.Document.Content.ToMemory().Span;
        var head = bytes[..Math.Min(bytes.Length, 1024)];

        var matches = extension switch
        {
            ".pdf" => head.IndexOf("%PDF-"u8) >= 0,
            ".png" => Starts(head, 0x89, (byte)'P', (byte)'N', (byte)'G'),
            ".jpg" or ".jpeg" => Starts(head, 0xFF, 0xD8, 0xFF),
            ".tif" or ".tiff" => Starts(head, (byte)'I', (byte)'I', (byte)'*', 0) || Starts(head, (byte)'M', (byte)'M', 0, (byte)'*'),
            ".txt" => LooksLikeText(bytes),
            ".eml" => LooksLikeText(bytes) && LooksLikeMailHeader(head),
            _ => true, // Other types are refused or read by the extractor itself.
        };

        if (matches)
        {
            return Task.FromResult<QualityFinding?>(null);
        }

        string? actual = null;
        foreach (var (magic, kind) in Binary)
        {
            if (head.StartsWith(magic))
            {
                actual = kind;
                break;
            }
        }

        var because = actual is null ? "its content is not of that type" : $"its content looks like {actual}";
        return Task.FromResult<QualityFinding?>(new(IssueCode.ProtectedOrCorruptDocument, Name,
            $"The file is named as a {extension} file but {because}, so it was not read. Ask the sender to send the original document."));
    }

    private static bool Starts(ReadOnlySpan<byte> head, params byte[] magic) => head.StartsWith(magic);

    /// <summary>No binary signature and no NUL byte in the first 8 KB: what UTF-8 (or ASCII) text looks like.</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        var head = bytes[..Math.Min(bytes.Length, 8192)];
        if (head.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        foreach (var (magic, _) in Binary)
        {
            if (head.StartsWith(magic))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first non-blank line is a header field (<c>Name: value</c>) or an mbox "From " line.</summary>
    private static bool LooksLikeMailHeader(ReadOnlySpan<byte> head)
    {
        var text = System.Text.Encoding.UTF8.GetString(head).TrimStart('﻿', '\r', '\n', ' ');
        var end = text.IndexOfAny(['\r', '\n']);
        var line = end >= 0 ? text[..end] : text;
        if (line.StartsWith("From ", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = line.IndexOf(':');
        return colon > 0 && line[..colon].All(c => c is >= '!' and <= '~' and not ':');
    }
}

/// <summary>
/// A PDF that cannot be opened, or is too big to read (INP-01). It opens the PDF once: it checks the structure, counts the pages
/// against <paramref name="maxPages"/> (default 50; the pilot scope is 10), and reads the text page by page with a deadline check
/// between pages, handing the text to <see cref="ParsedPdfCache"/> so the extractor does not parse the file a second time. A
/// PDF over the page limit is handed to a person without reading its text at all. Only the structure is reported, and the message
/// never repeats what the PDF library said, since that can quote content. A PDF with an owner password only still opens.
/// </summary>
public sealed class PdfIntegrityCheck(int maxPages = InputQualityOptions.DefaultMaxPages) : IInputQualityCheck
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
            var pageCount = pdf.NumberOfPages;
            if (pageCount > maxPages)
            {
                return Task.FromResult<QualityFinding?>(new(IssueCode.DocumentTooLarge, Name,
                    $"The PDF has {pageCount} pages, which is over the limit of {maxPages}. A person needs to read it."));
            }

            var pages = new List<string>(pageCount);
            foreach (var page in pdf.GetPages())
            {
                // PdfPig does not take a token, so the deadline is checked between pages.
                cancellationToken.ThrowIfCancellationRequested();
                pages.Add(LocalPdfTextExtractor.Normalise(ContentOrderTextExtractor.GetText(page)));
            }

            ParsedPdfCache.Store(context.Document, pages);
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

/// <summary>
/// The text of a PDF the integrity check has already read, handed to the extractor so the file is parsed once (INP-01). Keyed by the
/// document object, held weakly, and taken once: a different document, or a second call, parses afresh.
/// </summary>
public static class ParsedPdfCache
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IntakeDocument, IReadOnlyList<string>> Pages = new();

    public static void Store(IntakeDocument document, IReadOnlyList<string> pages) => Pages.AddOrUpdate(document, pages);

    public static bool TryTake(IntakeDocument document, out IReadOnlyList<string> pages)
    {
        if (Pages.TryGetValue(document, out var found))
        {
            Pages.Remove(document);
            pages = found;
            return true;
        }

        pages = [];
        return false;
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
                $"This file is identical to one already received (claim {ClaimsIntake.Domain.Intake.ClaimIds.ForDocument(earlier):D}).");
        return Task.FromResult(finding);
    }
}
