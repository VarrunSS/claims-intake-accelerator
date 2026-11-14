using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Domain.Intake;

/// <summary>
/// What the intake pipeline hands to the review store: the extracted fields, the validation issues and the
/// text they were read from. It is the message on the Service Bus queue and the body of the local HTTP
/// hand-off. The agent only ever proposes; a handler decides (ADR 0002).
/// </summary>
/// <param name="DocumentId">Identifier of the source document (the blob name, or the file name locally).</param>
/// <param name="ReceivedAt">When intake received the document.</param>
/// <param name="Fields">The extracted fields.</param>
/// <param name="Issues">Data-quality issues found by <see cref="ClaimValidator"/>.</param>
/// <param name="SourcePages">The text read from the document, one entry per page.</param>
/// <param name="Assessment">Coverage findings against the policy wording, when the pipeline ran an assessor.</param>
/// <param name="AuditTrail">The tool calls made while assessing. The API stores them in its audit log.</param>
/// <param name="NotifiedOn">The date the claim was notified: the date the document states, otherwise the date it was received.
/// Null when the input-quality gate stopped the document before extraction.</param>
/// <param name="NotifiedOnSource"><c>document</c> when the document states the date, <c>received</c> when it comes from the received timestamp.</param>
public sealed record ProposedClaim(
    string DocumentId,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<ExtractedField> Fields,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<string> SourcePages,
    ClaimAssessment? Assessment = null,
    IReadOnlyList<AuditEntry>? AuditTrail = null,
    DateOnly? NotifiedOn = null,
    string? NotifiedOnSource = null)
{
    /// <summary>JSON settings shared by every producer and consumer of the hand-off message.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// A claim id derived from the document id, so the same document handed over twice (a redelivered
    /// message, a retried upload) is recognised as one claim.
    /// </summary>
    public Guid ClaimId => ClaimIds.ForDocument(DocumentId);

    public Claim ToClaim()
    {
        var claim = new Claim(ClaimId, DocumentId, ReceivedAt, Fields, Issues, SourcePages);
        if (Assessment is not null)
        {
            claim.AttachAssessment(Assessment);
        }

        return claim;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ProposedClaim FromJson(string json) =>
        JsonSerializer.Deserialize<ProposedClaim>(json, JsonOptions)
        ?? throw new JsonException("The hand-off message was empty.");
}

public static class ClaimIds
{
    /// <summary>A stable id (the first 16 bytes of the SHA-256 of the document id).</summary>
    public static Guid ForDocument(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(documentId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
