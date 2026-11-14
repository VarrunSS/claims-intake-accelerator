using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Domain;

/// <summary>
/// A claim as it moves through intake. State changes go through Approve and Reject,
/// which enforce the human-in-the-loop rules from ADR 0002.
/// </summary>
public sealed class Claim
{
    private readonly Dictionary<string, ExtractedField> _fields;
    private readonly List<ValidationIssue> _issues;
    private readonly List<string> _sourcePages;

    public Claim(
        Guid id,
        string sourceDocumentId,
        DateTimeOffset receivedAt,
        IEnumerable<ExtractedField> fields,
        IEnumerable<ValidationIssue> issues,
        IEnumerable<string>? sourcePages = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A claim needs an id.", nameof(id));
        }

        Id = id;
        SourceDocumentId = !string.IsNullOrWhiteSpace(sourceDocumentId)
            ? sourceDocumentId
            : throw new ArgumentException("A claim needs a source document.", nameof(sourceDocumentId));
        ReceivedAt = receivedAt;
        _fields = (fields ?? throw new ArgumentNullException(nameof(fields)))
            .ToDictionary(f => f.Name, StringComparer.Ordinal);
        _issues = [.. issues ?? throw new ArgumentNullException(nameof(issues))];
        _sourcePages = [.. sourcePages ?? []];
        Status = ClaimStatus.NeedsReview;
    }

    public Guid Id { get; }
    public string SourceDocumentId { get; }
    public DateTimeOffset ReceivedAt { get; }
    public ClaimStatus Status { get; private set; }

    /// <summary>Who made the decision. Null until a handler approves or rejects.</summary>
    public string? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Why a handler rejected the claim. Null unless the claim was rejected.</summary>
    public string? RejectionReason { get; private set; }

    public IReadOnlyDictionary<string, ExtractedField> Fields => _fields;
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>
    /// The text read from the document, one entry per page, so a handler can check fields against it.
    /// Source spans index into these pages. This is personal data: show it to handlers, never log it.
    /// </summary>
    public IReadOnlyList<string> SourcePages => _sourcePages;

    /// <summary>Coverage findings against the policy wording. Null when no assessor ran. Advice for the handler only.</summary>
    public ClaimAssessment? Assessment { get; private set; }

    /// <summary>The reference the legacy claims system gave the claim. Null until the claim is submitted.</summary>
    public string? LegacyReference { get; private set; }

    /// <summary>The lowest confidence among the fields that were found. See <see cref="ClaimConfidence.Lowest"/>.</summary>
    public double MinConfidence => ClaimConfidence.Lowest(_fields.Values);

    public string? PolicyNumber => FieldValue(ClaimFields.PolicyNumber);
    public string? ClaimantName => FieldValue(ClaimFields.ClaimantName);

    /// <summary>True when any issue is an error. Such a claim can be rejected but not approved.</summary>
    public bool HasBlockingIssues => _issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Attaches the assessment made at intake. It never changes the status of the claim.</summary>
    public void AttachAssessment(ClaimAssessment assessment) =>
        Assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));

    /// <summary>Approves the claim. Only a claim awaiting review, with no blocking issues, can be approved.</summary>
    public void Approve(string reviewer, DateTimeOffset now)
    {
        RequireReviewer(reviewer);
        RequireNeedsReview("approved");
        if (HasBlockingIssues)
        {
            throw new InvalidClaimTransitionException(
                "The claim has data-quality errors and cannot be approved. Correct the fields or reject the claim.");
        }

        Status = ClaimStatus.Approved;
        DecidedBy = reviewer.Trim();
        DecidedAt = now;
    }

    /// <summary>Rejects the claim. A reason is required so the decision can be audited.</summary>
    public void Reject(string reviewer, string reason, DateTimeOffset now)
    {
        RequireReviewer(reviewer);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reject a claim.", nameof(reason));
        }

        RequireNeedsReview("rejected");

        Status = ClaimStatus.Rejected;
        DecidedBy = reviewer.Trim();
        DecidedAt = now;
        RejectionReason = reason.Trim();
    }

    /// <summary>Records that the approved claim was written to the legacy system, and under which reference.</summary>
    public void MarkSubmitted(string legacyReference)
    {
        if (string.IsNullOrWhiteSpace(legacyReference))
        {
            throw new ArgumentException("The legacy reference is required.", nameof(legacyReference));
        }

        if (Status != ClaimStatus.Approved)
        {
            throw new InvalidClaimTransitionException($"Only an approved claim can be submitted; this one is {Status}.");
        }

        Status = ClaimStatus.Submitted;
        LegacyReference = legacyReference.Trim();
    }

    private string? FieldValue(string name) =>
        _fields.TryGetValue(name, out var field) && field.HasValue ? field.Value!.Trim() : null;

    private void RequireNeedsReview(string verb)
    {
        if (Status != ClaimStatus.NeedsReview)
        {
            throw new InvalidClaimTransitionException($"The claim cannot be {verb} because it is already {Status}.");
        }
    }

    private static void RequireReviewer(string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new ArgumentException("The reviewer must be identified.", nameof(reviewer));
        }
    }
}
