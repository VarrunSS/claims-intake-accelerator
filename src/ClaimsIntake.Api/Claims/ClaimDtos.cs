using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Claims;

public sealed record ClaimSummary(
    Guid Id,
    string SourceDocumentId,
    string? PolicyNumber,
    string? ClaimantName,
    ClaimStatus Status,
    DateTimeOffset ReceivedAt,
    int ErrorCount,
    int WarningCount)
{
    public static ClaimSummary From(Claim claim) => new(
        claim.Id,
        claim.SourceDocumentId,
        claim.PolicyNumber,
        claim.ClaimantName,
        claim.Status,
        claim.ReceivedAt,
        claim.Issues.Count(i => i.Severity == IssueSeverity.Error),
        claim.Issues.Count(i => i.Severity == IssueSeverity.Warning));
}

public sealed record FieldDto(string Name, string? Value, double Confidence, SourceSpan? Source);

public sealed record IssueDto(IssueCode Code, IssueSeverity Severity, string? Field, string Message);

public sealed record ClaimDetail(
    Guid Id,
    string SourceDocumentId,
    ClaimStatus Status,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<FieldDto> Fields,
    IReadOnlyList<IssueDto> Issues,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? RejectionReason)
{
    public static ClaimDetail From(Claim claim) => new(
        claim.Id,
        claim.SourceDocumentId,
        claim.Status,
        claim.ReceivedAt,
        [.. claim.Fields.Values.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new FieldDto(f.Name, f.Value, f.Confidence, f.Source))],
        [.. claim.Issues.Select(i => new IssueDto(i.Code, i.Severity, i.Field, i.Message))],
        claim.DecidedBy,
        claim.DecidedAt,
        claim.RejectionReason);
}

public sealed record ApproveRequest(string? Reviewer);

public sealed record RejectRequest(string? Reviewer, string? Reason);
