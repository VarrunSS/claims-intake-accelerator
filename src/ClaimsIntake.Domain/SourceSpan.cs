namespace ClaimsIntake.Domain;

/// <summary>
/// Where in the source document a value was read from, so a handler can check it against the original.
/// </summary>
/// <param name="DocumentId">Identifier of the source document (the blob name at intake).</param>
/// <param name="Page">One-based page number.</param>
/// <param name="Offset">Zero-based character offset into the page text.</param>
/// <param name="Length">Length of the span in characters.</param>
public sealed record SourceSpan(string DocumentId, int Page, int Offset, int Length)
{
    public string DocumentId { get; } = !string.IsNullOrWhiteSpace(DocumentId)
        ? DocumentId
        : throw new ArgumentException("A source span needs a document id.", nameof(DocumentId));

    public int Page { get; } = Page >= 1
        ? Page
        : throw new ArgumentOutOfRangeException(nameof(Page), Page, "Pages are numbered from 1.");

    public int Offset { get; } = Offset >= 0
        ? Offset
        : throw new ArgumentOutOfRangeException(nameof(Offset), Offset, "Offset cannot be negative.");

    public int Length { get; } = Length >= 0
        ? Length
        : throw new ArgumentOutOfRangeException(nameof(Length), Length, "Length cannot be negative.");
}
