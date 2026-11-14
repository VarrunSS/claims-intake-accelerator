namespace ClaimsIntake.Pipeline.Text;

/// <summary>A document that arrived at intake: where it came from and its bytes.</summary>
/// <param name="DocumentId">Stable identifier, for example the blob name. Used in logs and telemetry.</param>
/// <param name="FileName">Name including the extension, which selects how local text extraction reads it.</param>
/// <param name="Content">The file contents.</param>
public sealed record IntakeDocument(string DocumentId, string FileName, BinaryData Content);

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

/// <summary>The document cannot be processed (wrong type, too large, unreadable). Retrying will not help.</summary>
public class UnreadableDocumentException(string message, Exception? inner = null) : Exception(message, inner);
