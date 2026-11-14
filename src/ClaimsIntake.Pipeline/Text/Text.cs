namespace ClaimsIntake.Pipeline.Text;

/// <summary>A document that arrived at intake: where it came from and its bytes.</summary>
/// <param name="DocumentId">Stable identifier, for example the blob name. Used in logs and telemetry.</param>
/// <param name="FileName">Name including the extension, which selects how local text extraction reads it.</param>
/// <param name="Content">The file contents.</param>
/// <param name="ReceivedAt">
/// When the document reached us: the blob's creation time in Azure, the file's creation time locally. It is the fallback
/// notification date when the document does not state one. When null the pipeline's clock is used.
/// </param>
public sealed record IntakeDocument(string DocumentId, string FileName, BinaryData Content, DateTimeOffset? ReceivedAt = null);

/// <summary>The text read from a document, one entry per page. Never log it: it is personal data.</summary>
public sealed record DocumentText(IReadOnlyList<string> Pages)
{
    public bool IsEmpty => Pages.All(string.IsNullOrWhiteSpace);
}

/// <summary>Turns a document into text. Local and Azure Document Intelligence implementations exist.</summary>
public interface IDocumentTextExtractor
{
    Task<DocumentText> ExtractAsync(IntakeDocument document, CancellationToken cancellationToken);
}

/// <summary>Why a document could not be read, when the extractor knows.</summary>
public enum UnreadableKind
{
    /// <summary>Anything else: wrong type, corrupt bytes.</summary>
    Other,

    /// <summary>The document was read but holds no text, for example a scan. The pipeline routes it to a person.</summary>
    NoText,
}

/// <summary>The document cannot be processed (wrong type, too large, unreadable). Retrying will not help.</summary>
public class UnreadableDocumentException(string message, Exception? inner = null) : Exception(message, inner)
{
    public UnreadableKind Kind { get; init; }
}
