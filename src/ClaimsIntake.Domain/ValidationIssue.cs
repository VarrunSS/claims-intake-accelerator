namespace ClaimsIntake.Domain;

public enum IssueSeverity
{
    /// <summary>Worth a look, but the claim can still be approved.</summary>
    Warning = 0,

    /// <summary>The claim cannot be approved until the problem is fixed.</summary>
    Error = 1,
}

/// <summary>Machine-readable reason for an issue. Stable, because dashboards and evals count by code.</summary>
public enum IssueCode
{
    MissingPolicyNumber,
    UnknownPolicy,
    MissingLossDate,
    UnreadableLossDate,
    LossDateInFuture,
    LossDateOutsidePolicyPeriod,
    LowConfidence,
    InvalidAmount,

    // Input quality (Milestone 4): the document is not a usable claim, so no fields were extracted. A person decides.

    /// <summary>No text could be read, or too little to be a claim: typically an unreadable scan.</summary>
    UnreadableDocument,

    /// <summary>The text is not English, or is not about a claim (marketing, an invoice, a newsletter).</summary>
    NotAClaim,

    /// <summary>The document is larger than the intake limit.</summary>
    DocumentTooLarge,

    /// <summary>The PDF is password protected or corrupt.</summary>
    ProtectedOrCorruptDocument,

    /// <summary>The same file (same SHA-256) was received before under another document id.</summary>
    DuplicateDocument,
}

/// <summary>A problem found by <see cref="ClaimValidator"/>.</summary>
/// <param name="Code">What kind of problem it is.</param>
/// <param name="Severity">Whether it blocks approval.</param>
/// <param name="Field">The field concerned, or null if the issue is about the claim as a whole.</param>
/// <param name="Message">Plain-English text for the handler.</param>
public sealed record ValidationIssue(IssueCode Code, IssueSeverity Severity, string? Field, string Message);
