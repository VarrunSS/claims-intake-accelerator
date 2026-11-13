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
}

/// <summary>A problem found by <see cref="ClaimValidator"/>.</summary>
/// <param name="Code">What kind of problem it is.</param>
/// <param name="Severity">Whether it blocks approval.</param>
/// <param name="Field">The field concerned, or null if the issue is about the claim as a whole.</param>
/// <param name="Message">Plain-English text for the handler.</param>
public sealed record ValidationIssue(IssueCode Code, IssueSeverity Severity, string? Field, string Message);
